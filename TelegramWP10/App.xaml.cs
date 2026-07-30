using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace TelegramWP10
{
    sealed partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
            // bglog.txt only records what BackgroundService writes, so a crash on
            // the UI thread leaves no trace. Log it before the process dies.
            this.UnhandledException += OnUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            this.Suspending += OnSuspending;
            this.Resuming += OnResuming;
            // Visibility transitions, which unlike Suspending/Resuming still
            // fire while an extended execution session defers suspension. They
            // decide whether a notification is allowed to make a sound.
            this.EnteredBackground += (s, e) => BackgroundService.NoteWentToBackground();
            this.LeavingBackground += (s, e) => BackgroundService.NoteComingToForeground();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }
            if (rootFrame.Content == null) rootFrame.Navigate(typeof(MainPage), e.Arguments);
            Window.Current.Activate();

            BackgroundService.NoteComingToForeground();
            // Регистрация идёт после Activate(), чтобы не задерживать показ окна.
            if (BackgroundService.CatchUpEnabled)
                await BackgroundService.RegisterCatchUpTaskAsync();
            else
                BackgroundService.UnregisterCatchUpTask(); // на случай если была включена раньше, а потом выключили
        }

        private void OnUnhandledException(object sender,
            Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                var ex = e.Exception;
                BackgroundService.Diag("CRASH: " + e.Message);
                if (ex != null)
                {
                    BackgroundService.Diag("CRASH type: " + ex.GetType().FullName);
                    if (ex.InnerException != null)
                        BackgroundService.Diag("CRASH inner: " + ex.InnerException.Message);
                    string st = ex.StackTrace ?? "";
                    // The log line cap keeps this readable; the top frames are
                    // the ones that matter.
                    if (st.Length > 600) st = st.Substring(0, 600);
                    BackgroundService.Diag("CRASH stack: " + st.Replace("\r\n", " | "));
                }
            }
            catch { }
            // Not marking Handled: swallowing it would hide the fault and leave
            // the app in an unknown state.
        }

        private void OnUnobservedTaskException(object sender,
            System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                BackgroundService.Diag("TASK EXCEPTION: " +
                    (e.Exception == null ? "(null)" : e.Exception.Message));
            }
            catch { }
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            BackgroundService.NoteWentToBackground();
            // Exit() raises Suspending too. A back-button exit is not a
            // minimise: there is nothing left to hold the process, and no reason to.
            if (BackgroundService.IsShuttingDown) { deferral.Complete(); return; }
            try
            {
                // Просим отсрочку. Дадут — соединение переживёт сворачивание;
                // не дадут — процесс замораживается как обычно.
                await BackgroundService.Instance.RequestGraceWindowAsync();
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void OnResuming(object sender, object e)
        {
            BackgroundService.NoteComingToForeground();
            BackgroundService.RequestForegroundHandover();
            BackgroundService.Instance.ReleaseGraceWindow();
        
            // Сессию могли отобрать, пока приложение было свёрнуто.
            if (BackgroundService.KeepAliveEnabled && !BackgroundService.Instance.KeepAliveActive)
                await BackgroundService.Instance.StartKeepAliveAsync();
        }

        protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);
            if (args.TaskInstance.Task.Name == BackgroundService.CatchUpTaskName)
                await BackgroundService.RunCatchUpAsync(args.TaskInstance);
        }
    }
}