﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Autofac.Core;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Autofac;


namespace Caliburn.Micro.Autofac
{
    public class AutofacBootstrapperBase : PhoneBootstrapperBase
    {
        readonly IDictionary<object, ViewScope> _viewsToScoped = new Dictionary<object, ViewScope>();

        public AutofacBootstrapperBase()
        {
            StartRuntime();
        }

        protected IContainer Container { get; private set; }

        /// <summary>
        /// Should the namespace convention be enforced for type registration. The default is true.
        /// For views, this would require a views namespace to end with Views
        /// For view-models, this would require a view models namespace to end with ViewModels
        /// <remarks>Case is important as views would not match.</remarks>
        /// </summary>
        public bool EnforceNamespaceConvention { get; set; }

        /// <summary>
        /// Should the view be treated as loaded when registering the INavigationService.
        /// </summary>
        public bool TreatViewAsLoaded { get; set; }

        /// <summary>
        /// The base type required for a view model
        /// </summary>
        public Type ViewModelBaseType { get; set; }

        /// <summary>
        /// Method for creating the window manager
        /// </summary>
        public Func<IWindowManager> CreateWindowManager { get; set; }

        /// <summary>
        /// Method for creating the event aggregator
        /// </summary>
        public Func<IEventAggregator> CreateEventAggregator { get; set; }

        //  Method for creating the frame adapter
        public Func<FrameAdapter> CreateFrameAdapter { get; set; }
        //  Method for creating the phone application service adapter
        public Func<PhoneApplicationServiceAdapter> CreatePhoneApplicationServiceAdapter { get; set; }
        //  Method for creating the vibrate controller
        public Func<IVibrateController> CreateVibrateController { get; set; }
        //  Method for creating the sound effect player
        public Func<ISoundEffectPlayer> CreateSoundEffectPlayer { get; set; }

        /// <summary>
        /// Do not override this method. This is where the IoC container is configured.
        /// <remarks>
        /// Will throw <see cref="System.ArgumentNullException"/> is either CreateWindowManager
        /// or CreateEventAggregator is null.
        /// </remarks>
        /// </summary>
        protected override void Configure()
        {
            //  allow base classes to change bootstrapper settings
            ConfigureBootstrapper();

            //  validate settings
            if (CreateFrameAdapter == null)
                throw new ArgumentNullException("CreateFrameAdapter");
            if (CreateWindowManager == null)
                throw new ArgumentNullException("CreateWindowManager");
            if (CreateEventAggregator == null)
                throw new ArgumentNullException("CreateEventAggregator");
            if (CreatePhoneApplicationServiceAdapter == null)
                throw new ArgumentNullException("CreatePhoneApplicationServiceAdapter");
            if (CreateVibrateController == null)
                throw new ArgumentNullException("CreateVibrateController");
            if (CreateSoundEffectPlayer == null)
                throw new ArgumentNullException("CreateSoundEffectPlayer");

            //  configure container
            var builder = new ContainerBuilder();

            //  register phone services
            var caliburnAssembly = typeof(IStorageMechanism).Assembly;

            builder.RegisterAssemblyTypes(caliburnAssembly)
                .Where(type => typeof(IStorageMechanism).IsAssignableFrom(type)
                               && !type.IsAbstract
                               && !type.IsInterface)
                .As<IStorageMechanism>()
                .InstancePerLifetimeScope();

            builder.RegisterAssemblyTypes(AssemblySource.Instance.ToArray())
                .Where(type => typeof(IStorageHandler).IsAssignableFrom(type)
                               && !type.IsAbstract
                               && !type.IsInterface)
                .As<IStorageHandler>()
                .InstancePerLifetimeScope();

            //  register view models
            builder.RegisterAssemblyTypes(AssemblySource.Instance.ToArray())
                .Where(type => type.Name.EndsWith("ViewModel"))
                .Where(
                    type =>
                        EnforceNamespaceConvention
                            ? (!(string.IsNullOrEmpty(type.Namespace)) && type.Namespace.EndsWith("ViewModels"))
                            : true)
                .Where(type => type.GetInterface(ViewModelBaseType.Name, false) != null)
                .AsSelf()
                .InstancePerDependency()
                .OnActivated(x => x.Context.Resolve<AutofacPhoneContainer>().OnActivated(x.Instance));

            //  register views
            builder.RegisterAssemblyTypes(AssemblySource.Instance.ToArray())
                .Where(type => type.Name.EndsWith("View"))
                .Where(
                    type =>
                        EnforceNamespaceConvention
                            ? (!(string.IsNullOrEmpty(type.Namespace)) && type.Namespace.EndsWith("Views"))
                            : true)
                .AsSelf()
                .InstancePerDependency();

            // The constructor of these services must be called
            // to attach to the framework properly.
            var phoneService = CreatePhoneApplicationServiceAdapter();
            var navigationService = CreateFrameAdapter();

            //  register the singletons
            builder.Register(c => new AutofacPhoneContainer(c))
                .As<AutofacPhoneContainer>()
                .As<IPhoneContainer>()
                .SingleInstance();
            builder.RegisterInstance<INavigationService>(navigationService).SingleInstance();
            builder.RegisterInstance<IPhoneService>(phoneService).SingleInstance();
            builder.Register<IEventAggregator>(c => CreateEventAggregator()).SingleInstance();
            builder.Register<IWindowManager>(c => CreateWindowManager()).InstancePerLifetimeScope();
            builder.Register<IVibrateController>(c => CreateVibrateController()).InstancePerLifetimeScope();
            builder.Register<ISoundEffectPlayer>(c => CreateSoundEffectPlayer()).InstancePerLifetimeScope();
            builder.RegisterType<StorageCoordinator>().AsSelf().InstancePerLifetimeScope();
            builder.RegisterType<TaskController>().AsSelf().InstancePerLifetimeScope();

            builder.RegisterAssemblyModules(SelectAssemblies().ToArray());

            //  allow derived classes to add to the container
            ConfigureContainer(builder);

            //  build the container
            Container = builder.Build();

            //  start services
            Container.Resolve<StorageCoordinator>().Start();
            Container.Resolve<TaskController>().Start();

            //  add custom conventions for the phone
            AddCustomConventions();

            BeginLifetimeScope();
        }

        private void BeginLifetimeScope()
        {
            var old = ViewModelLocator.LocateForView;
            ViewModelLocator.LocateForView = view =>
            {
                var page = view as FrameworkElement;
                if (page != null)
                {
                    var key = RootFrame.Source.ToString();

                    if (_viewsToScoped.ContainsKey(key) == false)
                    {
                        var scope = Container.BeginLifetimeScope(x => x.RegisterInstance(view)
                            .AsSelf()
                            .AsImplementedInterfaces());
                        _viewsToScoped[key] = new ViewScope(page, scope);
                    }
                    else
                    {
                        if (_viewsToScoped[key].View != page)
                            throw new Exception("View instance is different to the view already registered at that Uri.");
                    }
                }

                return old(view);
            };
        }

        private void FrameOnJournalEntryRemoved(object sender, JournalEntryRemovedEventArgs e)
        {
            ViewScope scope;
            var key = e.Entry.Source.ToString();
            if (_viewsToScoped.TryGetValue(key, out scope))
            {
                scope.LifetimeScope.Dispose();
                _viewsToScoped.Remove(key);
            }
        }

        /// <summary>
        /// Do not override unless you plan to full replace the logic. This is how the framework
        /// retrieves services from the Autofac container.
        /// </summary>
        /// <param name="service">The service to locate.</param>
        /// <param name="key">The key to locate.</param>
        /// <returns>The located service.</returns>
        protected override object GetInstance(System.Type service, string key)
        {
            ViewScope scope;
            if (!_viewsToScoped.TryGetValue(RootFrame.Source.ToString(), out scope))
                throw new DependencyResolutionException("No matching lifetime scope to resolve with");

            object instance;
            if (string.IsNullOrEmpty(key))
            {
                if (scope.LifetimeScope.TryResolve(service, out instance))
                    return instance;
            }
            else
            {
                if (scope.LifetimeScope.TryResolveNamed(key, service, out instance))
                    return instance;
            }
            throw new Exception(string.Format("Could not locate any instances of contract {0}.", key ?? service.Name));
        }

        /// <summary>
        /// Do not override unless you plan to full replace the logic. This is how the framework
        /// retrieves services from the Autofac container.
        /// </summary>
        /// <param name="service">The service to locate.</param>
        /// <returns>The located services.</returns>
        protected override IEnumerable<object> GetAllInstances(System.Type service)
        {
            ViewScope scope;
            if (!_viewsToScoped.TryGetValue(RootFrame.Source.ToString(), out scope))
                throw new DependencyResolutionException("No matching lifetime scope to resolve with");

            return scope.LifetimeScope.Resolve(typeof(IEnumerable<>).MakeGenericType(service)) as IEnumerable<object>;
        }

        /// <summary>
        /// Do not override unless you plan to full replace the logic. This is how the framework
        /// retrieves services from the Autofac container.
        /// </summary>
        /// <param name="instance">The instance to perform injection on.</param>
        protected override void BuildUp(object instance)
        {
            Container.InjectProperties(instance);
        }

        protected override void PrepareApplication()
        {
            base.PrepareApplication();
            RootFrame.JournalEntryRemoved += FrameOnJournalEntryRemoved;
            RootFrame.Navigating += FrameOnNavigating;
            RootFrame.Navigated += FrameOnNavigated;
        }

        /// <summary>
        /// Enable fast app resume support.
        /// This allows tapping the main tile to resume at the screen the user was on before switching away from the app.
        /// </summary>
        public void EnableFastAppResumeSupport(Uri mainScreenUri)
        {
            _mainScreen = mainScreenUri;
        }

        private void FrameOnNavigated(object sender, NavigationEventArgs e)
        {
            if (_mainScreen != null)
                _shouldReset = e.NavigationMode == NavigationMode.Reset;
        }

        private Uri _mainScreen;
        private bool _shouldReset;
        private void FrameOnNavigating(object sender, NavigatingCancelEventArgs e)
        {
            if (_mainScreen == null) return;
            if (_shouldReset == false || e.IsCancelable == false) return;

            var mainPageUri = _mainScreen.ToString().TrimStart('/');
            if (e.Uri.OriginalString.TrimStart('/') == mainPageUri)
            {
                e.Cancel = true;
                _shouldReset = false;
            }

            _shouldReset = false;
        }

        /// <summary>
        /// Override to provide configuration prior to the Autofac configuration. You must call the base version BEFORE any 
        /// other statement or the behaviour is undefined.
        /// Current Defaults:
        ///   EnforceNamespaceConvention = true
        ///   TreatViewAsLoaded = false
        ///   ViewModelBaseType = <see cref="System.ComponentModel.INotifyPropertyChanged"/> 
        ///   CreateWindowManager = <see cref="Caliburn.Micro.WindowManager"/> 
        ///   CreateEventAggregator = <see cref="Caliburn.Micro.EventAggregator"/>
        ///   CreateFrameAdapter = <see cref="Caliburn.Micro.FrameAdapter"/>
        ///   CreatePhoneApplicationServiceAdapter = <see cref="Caliburn.Micro.PhoneApplicationServiceAdapter"/>
        ///   CreateVibrateController = <see cref="Caliburn.Micro.SystemVibrateController"/>
        ///   CreateSoundEffectPlayer = <see cref="Caliburn.Micro.XnaSoundEffectPlayer"/>
        /// </summary>
        protected virtual void ConfigureBootstrapper()
        {
            EnforceNamespaceConvention = false;
            TreatViewAsLoaded = false;

            ViewModelBaseType = typeof(System.ComponentModel.INotifyPropertyChanged);
            CreateWindowManager = () => new WindowManager();
            CreateEventAggregator = () => new EventAggregator();
            CreateFrameAdapter = () => new FrameAdapter(RootFrame, TreatViewAsLoaded);
            CreatePhoneApplicationServiceAdapter =
                () => new PhoneApplicationServiceAdapter(PhoneApplicationService.Current, RootFrame);
            CreateVibrateController = () => new SystemVibrateController();
            CreateSoundEffectPlayer = () => new XnaSoundEffectPlayer();
        }

        /// <summary>
        /// Override to include your own Autofac configuration after the framework has finished its configuration, but 
        /// before the container is created.
        /// </summary>
        /// <param name="builder">The Autofac configuration builder.</param>
        protected virtual void ConfigureContainer(ContainerBuilder builder)
        {
        }

        private static void AddCustomConventions()
        {
            ConventionManager.AddElementConvention<Pivot>(Pivot.ItemsSourceProperty, "SelectedItem", "SelectionChanged")
                .ApplyBinding =
                (viewModelType, path, property, element, convention) =>
                {
                    if (ConventionManager
                        .GetElementConvention(typeof(ItemsControl))
                        .ApplyBinding(viewModelType, path, property, element, convention))
                    {
                        ConventionManager
                            .ConfigureSelectedItem(element, Pivot.SelectedItemProperty, viewModelType, path);
                        ConventionManager
                            .ApplyHeaderTemplate(element, Pivot.HeaderTemplateProperty, null, viewModelType);
                        return true;
                    }

                    return false;
                };

            ConventionManager.AddElementConvention<Panorama>(Panorama.ItemsSourceProperty, "SelectedItem",
                "SelectionChanged").ApplyBinding =
                (viewModelType, path, property, element, convention) =>
                {
                    if (ConventionManager
                        .GetElementConvention(typeof(ItemsControl))
                        .ApplyBinding(viewModelType, path, property, element, convention))
                    {
                        ConventionManager
                            .ConfigureSelectedItem(element, Panorama.SelectedItemProperty, viewModelType, path);
                        ConventionManager
                            .ApplyHeaderTemplate(element, Panorama.HeaderTemplateProperty, null, viewModelType);
                        return true;
                    }

                    return false;
                };
        }
    }
}
