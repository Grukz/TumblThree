﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Waf.Applications;
using System.Windows.Threading;

using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Applications.ViewModels;
using TumblThree.Domain;
using TumblThree.Domain.Queue;

namespace TumblThree.Applications.Controllers
{
    [Export(typeof(IModuleController)), Export]
    internal class ModuleController : IModuleController
    {
        private const string appSettingsFileName = "Settings.json";
        private const string managerSettingsFileName = "Manager.json";
        private const string queueSettingsFileName = "Queuelist.json";
        private const string cookiesFileName = "Cookies.json";

        private readonly ISharedCookieService cookieService;
        private readonly IEnvironmentService environmentService;
        private readonly Lazy<ShellService> shellService;

        private readonly Lazy<CrawlerController> crawlerController;
        private readonly Lazy<DetailsController> detailsController;
        private readonly Lazy<ManagerController> managerController;
        private readonly Lazy<QueueController> queueController;

        private readonly QueueManager queueManager;
        private readonly ISettingsProvider settingsProvider;
        private readonly IConfirmTumblrPrivacyConsent confirmTumblrPrivacyConsent;

        private readonly Lazy<ShellViewModel> shellViewModel;

        private AppSettings appSettings;
        private ManagerSettings managerSettings;
        private QueueSettings queueSettings;
        private List<Cookie> cookieList;

        [ImportingConstructor]
        public ModuleController(Lazy<ShellService> shellService, IEnvironmentService environmentService,
            IConfirmTumblrPrivacyConsent confirmTumblrPrivacyConsent, ISettingsProvider settingsProvider,
            ISharedCookieService cookieService, Lazy<ManagerController> managerController, Lazy<QueueController> queueController,
            Lazy<DetailsController> detailsController,
            Lazy<CrawlerController> crawlerController, Lazy<ShellViewModel> shellViewModel)
        {
            this.shellService = shellService;
            this.environmentService = environmentService;
            this.confirmTumblrPrivacyConsent = confirmTumblrPrivacyConsent;
            this.settingsProvider = settingsProvider;
            this.cookieService = cookieService;
            this.detailsController = detailsController;
            this.managerController = managerController;
            this.queueController = queueController;
            this.crawlerController = crawlerController;
            this.shellViewModel = shellViewModel;
            queueManager = new QueueManager();
        }

        private ShellService ShellService => shellService.Value;

        private ManagerController ManagerController => managerController.Value;

        private QueueController QueueController => queueController.Value;

        private DetailsController DetailsController => detailsController.Value;

        private CrawlerController CrawlerController => crawlerController.Value;

        private ShellViewModel ShellViewModel => shellViewModel.Value;

        public void Initialize()
        {
            string savePath = environmentService.AppSettingsPath;
            if (CheckIfPortableMode(appSettingsFileName))
                savePath = AppDomain.CurrentDomain.BaseDirectory;

            appSettings = LoadSettings<AppSettings>(Path.Combine(savePath, appSettingsFileName));
            queueSettings = LoadSettings<QueueSettings>(Path.Combine(savePath, queueSettingsFileName));
            managerSettings = LoadSettings<ManagerSettings>(Path.Combine(savePath, managerSettingsFileName));
            cookieList = LoadSettings<List<Cookie>>(Path.Combine(savePath, cookiesFileName));

            ShellService.Settings = appSettings;
            ShellService.ShowErrorAction = ShellViewModel.ShowError;
            ShellService.ShowDetailsViewAction = ShowDetailsView;
            ShellService.ShowQueueViewAction = ShowQueueView;
            ShellService.UpdateDetailsViewAction = UpdateDetailsView;
            ShellService.SettingsUpdatedHandler += OnSettingsUpdated;
            ShellService.InitializeOAuthManager();

            ManagerController.QueueManager = queueManager;
            ManagerController.ManagerSettings = managerSettings;
            ManagerController.BlogManagerFinishedLoadingLibrary += OnBlogManagerFinishedLoadingLibrary;
            QueueController.QueueSettings = queueSettings;
            QueueController.QueueManager = queueManager;
            DetailsController.QueueManager = queueManager;
            CrawlerController.QueueManager = queueManager;

            Task managerControllerInit = ManagerController.InitializeAsync();
            QueueController.Initialize();
            DetailsController.Initialize();
            CrawlerController.Initialize();
            cookieService.SetUriCookie(cookieList);
        }

        public async void Run()
        {
            ShellViewModel.IsQueueViewVisible = true;
            ShellViewModel.Show();

            // Let the UI to initialize first before loading the queuelist.
            await Dispatcher.CurrentDispatcher.InvokeAsync(ManagerController.RestoreColumn, DispatcherPriority.ApplicationIdle);
            await Dispatcher.CurrentDispatcher.InvokeAsync(QueueController.Run, DispatcherPriority.ApplicationIdle);
            await confirmTumblrPrivacyConsent.ConfirmPrivacyConsentAsync();
        }

        public void Shutdown()
        {
            DetailsController.Shutdown();
            QueueController.Shutdown();
            ManagerController.Shutdown();
            CrawlerController.Shutdown();

            SaveSettings();
        }

        private void SaveSettings()
        {
            string savePath = environmentService.AppSettingsPath;
            if (appSettings.PortableMode)
                savePath = AppDomain.CurrentDomain.BaseDirectory;

            SaveSettings(Path.Combine(savePath, appSettingsFileName), appSettings);
            SaveSettings(Path.Combine(savePath, queueSettingsFileName), queueSettings);
            SaveSettings(Path.Combine(savePath, managerSettingsFileName), managerSettings);
            SaveSettings(Path.Combine(savePath, cookiesFileName), new List<Cookie>(cookieService.GetAllCookies()));
        }

        private void OnSettingsUpdated(object sender, EventArgs e)
        {
            SaveSettings();
        }

        private void OnBlogManagerFinishedLoadingLibrary(object sender, EventArgs e)
        {
            QueueController.LoadQueue();
        }

        private static bool CheckIfPortableMode(string fileName)
        {
            return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName));
        }

        private T LoadSettings<T>(string fileName) where T : class, new()
        {
            try
            {
                return settingsProvider.LoadSettings<T>(fileName);
            }
            catch (Exception ex)
            {
                Logger.Error("Could not read the settings file: {0}", ex);
                return new T();
            }
        }

        private void SaveSettings(string fileName, object settings)
        {
            try
            {
                settingsProvider.SaveSettings(fileName, settings);
            }
            catch (Exception ex)
            {
                Logger.Error("Could not save the settings file: {0}", ex);
            }
        }

        private void ShowDetailsView()
        {
            ShellViewModel.IsDetailsViewVisible = true;
        }

        private void ShowQueueView()
        {
            ShellViewModel.IsQueueViewVisible = true;
        }

        private void UpdateDetailsView()
        {
            if (!ShellViewModel.IsQueueViewVisible)
                ShellViewModel.IsDetailsViewVisible = true;
        }
    }
}
