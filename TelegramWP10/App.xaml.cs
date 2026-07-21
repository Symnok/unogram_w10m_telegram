using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace TelegramWP10 {
    sealed partial class App : Application {
        public App() {
            this.InitializeComponent();
            this.Suspending += App_Suspending;
            this.Resuming += App_Resuming;
        }

        private async void App_Suspending(object sender, SuspendingEventArgs e) {
            var deferral = e.SuspendingOperation.GetDeferral();
            try {
                // Закрываем TDLib чтобы освободить файлы БД для BackgroundTask
                var page = GetMainPage();
                if (page != null) await page.SuspendTdLib();
            } finally {
                deferral.Complete();
            }
        }

        private void App_Resuming(object sender, object e) {
            // Возобновляем TDLib после resume
            var page = GetMainPage();
            page?.ResumeTdLib();
        }

        private MainPage GetMainPage() {
            var frame = Window.Current?.Content as Frame;
            return frame?.Content as MainPage;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e) {
            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null) {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }
            if (rootFrame.Content == null) rootFrame.Navigate(typeof(MainPage), e.Arguments);
            Window.Current.Activate();
        }
    }
}
