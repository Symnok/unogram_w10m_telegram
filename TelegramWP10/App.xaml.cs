using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace TelegramWP10 {
    sealed partial class App : Application {
        public App() {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
            this.Resuming += OnResuming;
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e) {
            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null) {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }
            if (rootFrame.Content == null) rootFrame.Navigate(typeof(MainPage), e.Arguments);
            Window.Current.Activate();

            BackgroundService.IsInForeground = true;
            // Регистрация идёт после Activate(), чтобы не задерживать показ окна.
            await BackgroundService.RegisterCatchUpTaskAsync();
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e) {
            var deferral = e.SuspendingOperation.GetDeferral();
            BackgroundService.IsInForeground = false;
            try {
                // Просим отсрочку. Дадут — соединение переживёт сворачивание;
                // не дадут — процесс замораживается как обычно.
                await BackgroundService.Instance.RequestGraceWindowAsync();
            } finally {
                deferral.Complete();
            }
        }

        private void OnResuming(object sender, object e) {
            BackgroundService.IsInForeground = true;
            BackgroundService.RequestForegroundHandover();
            BackgroundService.Instance.ReleaseGraceWindow();
        }

        protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args) {
            base.OnBackgroundActivated(args);
            if (args.TaskInstance.Task.Name == BackgroundService.CatchUpTaskName)
                await BackgroundService.RunCatchUpAsync(args.TaskInstance);
        }
    }
}
