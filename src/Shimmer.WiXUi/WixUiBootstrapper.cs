using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Text;
using System.Windows;
using Microsoft.Tools.WindowsInstallerXml.Bootstrapper;
using NuGet;
using ReactiveUI;
using ReactiveUI.Routing;
using ReactiveUI.Xaml;
using Shimmer.Client;
using Shimmer.Client.WiXUi;
using Shimmer.Core;
using Shimmer.WiXUi.Views;
using TinyIoC;

using Path = System.IO.Path;
using FileNotFoundException = System.IO.FileNotFoundException;

namespace Shimmer.WiXUi.ViewModels
{
    public class WixUiBootstrapper : ReactiveObject, IWixUiBootstrapper
    {
        public IRoutingState Router { get; protected set; }
        public IWiXEvents WiXEvents { get; protected set; }
        public static TinyIoCContainer Kernel { get; protected set; }

        readonly Lazy<ReleaseEntry> _BundledRelease;
        public ReleaseEntry BundledRelease { get { return _BundledRelease.Value; } }

        readonly Lazy<IPackage> bundledPackageMetadata;

        readonly IFileSystemFactory fileSystem;
        readonly string currentAssemblyDir;

        public WixUiBootstrapper(IWiXEvents wixEvents, TinyIoCContainer testKernel = null, IRoutingState router = null, IFileSystemFactory fileSystem = null, string currentAssemblyDir = null)
        {
            Kernel = testKernel ?? createDefaultKernel();
            this.fileSystem = fileSystem ?? AnonFileSystem.Default;
            this.currentAssemblyDir = currentAssemblyDir ?? Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

            RxApp.ConfigureServiceLocator(
                (type, contract) => String.IsNullOrEmpty(contract) ?
                    Kernel.Resolve(type) :
                    Kernel.Resolve(type, contract),
                (type, contract) => Kernel.ResolveAll(type, true),
                (c, t, s) => {
                    if (String.IsNullOrEmpty(s)) {
                        Kernel.Register(t, c, Guid.NewGuid().ToString());
                    } else {
                        Kernel.Register(t, c, s);
                    }
                });

            RxRouting.ViewModelToViewFunc = findViewClassNameForViewModelName;

            Kernel.Register<IWixUiBootstrapper>(this);
            Kernel.Register<IScreen>(this);
            Kernel.Register(wixEvents);

            Router = router ?? new RoutingState();
            WiXEvents = wixEvents;

            _BundledRelease = new Lazy<ReleaseEntry>(readBundledReleasesFile);

            registerExtensionDlls(Kernel);

            UserError.RegisterHandler(ex => {
                if (wixEvents.DisplayMode != Display.Full) {
                    this.Log().Error(ex.ErrorMessage);
                    wixEvents.ShouldQuit();
                }

                var errorVm = RxApp.GetService<IErrorViewModel>();
                errorVm.Error = ex;
                    
                RxApp.DeferredScheduler.Schedule(() => Router.Navigate.Execute(errorVm));
                return Observable.Return(RecoveryOptionResult.CancelOperation);
            });

            bundledPackageMetadata = new Lazy<IPackage>(openBundledPackage);

            wixEvents.DetectPackageCompleteObs.Subscribe(eventArgs => {
                var error = convertHResultToError(eventArgs.Status);
                if (error != null) {
                    UserError.Throw(error);
                    return;
                }

                if (wixEvents.Action == LaunchAction.Uninstall) {
                    var uninstallVm = RxApp.GetService<IUninstallingViewModel>();
                    Router.Navigate.Execute(uninstallVm);
                    wixEvents.Engine.Plan(LaunchAction.Uninstall);
                    return;
                }

                // TODO: If the app is already installed, run it and bail
                // If Display is silent, we should just exit here.

                if (wixEvents.Action == LaunchAction.Install) {
                    if (wixEvents.DisplayMode != Display.Full) {
                        wixEvents.Engine.Plan(LaunchAction.Install);
                        return;
                    }

                    var welcomeVm = RxApp.GetService<IWelcomeViewModel>();
                    welcomeVm.PackageMetadata = bundledPackageMetadata.Value;
                    welcomeVm.ShouldProceed.Subscribe(_ => wixEvents.Engine.Plan(LaunchAction.Install));
                    
                    // NB: WiX runs a "Main thread" that all of these events 
                    // come back on, and a "UI thread" where it actually runs
                    // the WPF window. Gotta proxy to the UI thread.
                    RxApp.DeferredScheduler.Schedule(() => Router.Navigate.Execute(welcomeVm));
                }
            });

            var executablesToStart = Enumerable.Empty<string>();

            wixEvents.PlanCompleteObs.Subscribe(eventArgs => {
                var installManager = new InstallManager(BundledRelease);
                var error = convertHResultToError(eventArgs.Status);
                if (error != null) {
                    UserError.Throw(error);
                    return;
                }

                if (wixEvents.Action == LaunchAction.Uninstall) {
                    installManager.ExecuteUninstall().Subscribe(
                        _ => wixEvents.Engine.Apply(wixEvents.MainWindowHwnd),
                        ex => UserError.Throw(new UserError("Failed to uninstall", ex.Message, innerException: ex)));
                    return;
                }

                IObserver<int> progress = null;

                if (wixEvents.DisplayMode == Display.Full) {
                    var installingVm = RxApp.GetService<IInstallingViewModel>();
                    progress = installingVm.ProgressValue;
                    installingVm.PackageMetadata = bundledPackageMetadata.Value;
                    RxApp.DeferredScheduler.Schedule(() => Router.Navigate.Execute(installingVm));
                }

                installManager.ExecuteInstall(this.currentAssemblyDir, bundledPackageMetadata.Value, progress).Subscribe(
                    toStart => {
                        executablesToStart = toStart ?? executablesToStart;
                        wixEvents.Engine.Apply(wixEvents.MainWindowHwnd);
                    },
                    ex => UserError.Throw("Failed to install application", ex));
            });

            wixEvents.ApplyCompleteObs.Subscribe(eventArgs => {
                var error = convertHResultToError(eventArgs.Status);
                if (error != null) {
                    UserError.Throw(error);
                    return;
                }

                if (wixEvents.DisplayMode == Display.Full && wixEvents.Action == LaunchAction.Install) {
                    foreach (var path in executablesToStart) { Process.Start(path); }
                }

                wixEvents.ShouldQuit();
            });

            wixEvents.ErrorObs.Subscribe(eventArgs => UserError.Throw("An installation error has occurred: " + eventArgs.ErrorMessage));

            wixEvents.Engine.Detect();
        }

        UserError convertHResultToError(int status)
        {
            // NB: WiX passes this as an int which makes it impossible for us to
            // grok properly
            var hr = BitConverter.ToUInt32(BitConverter.GetBytes(status), 0);
            if ((hr & 0x80000000) == 0) {
                return null;
            }

            return new UserError(String.Format("An installer error has occurred: 0x{0:x}", hr));
        }

        IPackage openBundledPackage()
        {
            var fi = fileSystem.GetFileInfo(Path.Combine(currentAssemblyDir, BundledRelease.Filename));
            return new ZipPackage(fi.FullName);
        }

        ReleaseEntry readBundledReleasesFile()
        {
            var release = fileSystem.GetFileInfo(Path.Combine(currentAssemblyDir, "RELEASES"));

            if (!release.Exists) {
                UserError.Throw("This installer is incorrectly configured, please contact the author", 
                    new FileNotFoundException(release.FullName));
                return null;
            }

            ReleaseEntry ret;

            try {
                var fileText = fileSystem.GetFile(release.FullName).ReadAllText(release.FullName, Encoding.UTF8);
                ret = ReleaseEntry.ParseReleaseFile(fileText).OrderByDescending(x => x.Version).First();
            } catch (Exception ex) {
                this.Log().ErrorException("Couldn't read bundled RELEASES file", ex);
                UserError.Throw("This installer is incorrectly configured, please contact the author", ex);
                return null;
            }

            return ret;
        }

        void registerExtensionDlls(TinyIoCContainer kernel)
        {
            var di = fileSystem.GetDirectoryInfo(Path.GetDirectoryName(currentAssemblyDir));

            var thisAssembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var extensions = di.GetFiles("*.dll")
                .Where(x => x.FullName != thisAssembly)
                .SelectMany(x => {
                    try {
                        return new[] { System.Reflection.Assembly.LoadFile(x.FullName) };
                    } catch (Exception ex) {
                        this.Log().WarnException("Couldn't load " + x.Name, ex);
                        return Enumerable.Empty<System.Reflection.Assembly>();
                    }
                })
                .SelectMany(x => x.GetModules()).SelectMany(x => x.GetTypes())
                .Where(x => typeof(IWiXCustomUi).IsAssignableFrom(x) && !x.IsAbstract)
                .SelectMany(x => {
                    try {
                        return new[] {(IWiXCustomUi) Activator.CreateInstance(x)};
                    } catch (Exception ex) {
                        this.Log().WarnException("Couldn't create instance: " + x.FullName, ex);
                        return Enumerable.Empty<IWiXCustomUi>();
                    }
                });

            foreach (var extension in extensions) {
                extension.RegisterTypes(kernel);
            }

            registerDefaultTypes(kernel);
        }

        static void registerDefaultTypes(TinyIoCContainer kernel)
        {
            var toRegister = new[] {
                new { Interface = typeof(IErrorViewModel), Impl = typeof(ErrorViewModel) },
                new { Interface = typeof(IWelcomeViewModel), Impl = typeof(WelcomeViewModel) },
                new { Interface = typeof(IInstallingViewModel), Impl = typeof(InstallingViewModel) },
                new { Interface = typeof(IUninstallingViewModel), Impl = typeof(UninstallingViewModel) },
                new { Interface = typeof(IViewFor<IErrorViewModel>), Impl = typeof(ErrorView) },
                new { Interface = typeof(IViewFor<IWelcomeViewModel>), Impl = typeof(WelcomeView) },
                new { Interface = typeof(IViewFor<IInstallingViewModel>), Impl = typeof(InstallingView) },
                new { Interface = typeof(IViewFor<IUninstallingViewModel>), Impl = typeof(UninstallingView) },
            };

            foreach (var pair in toRegister.Where(pair => !kernel.CanResolve(pair.Interface))) {
                kernel.Register(pair.Interface, pair.Impl);
            }
        }

        static string findViewClassNameForViewModelName(string viewModelName)
        {
            if (viewModelName.Contains("ErrorViewModel")) return typeof (IViewFor<IErrorViewModel>).AssemblyQualifiedName;
            if (viewModelName.Contains("WelcomeViewModel")) return typeof (IViewFor<IWelcomeViewModel>).AssemblyQualifiedName;
            if (viewModelName.Contains("InstallingViewModel")) return typeof (IViewFor<IInstallingViewModel>).AssemblyQualifiedName;
            if (viewModelName.Contains("UninstallingViewModel")) return typeof (IViewFor<IUninstallingViewModel>).AssemblyQualifiedName;

            throw new Exception("Unknown View");
        }

        TinyIoCContainer createDefaultKernel()
        {
            var ret = new TinyIoCContainer();
            return ret;
        }
    }
}
