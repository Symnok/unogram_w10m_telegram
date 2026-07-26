using System.ComponentModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace TelegramWP10
{
    public class ContactItem : INotifyPropertyChanged
    {
        public long UserId { get; set; }

        private string _fullName = "";
        public string FullName {
            get => _fullName;
            set { _fullName = value; OnPropertyChanged("FullName"); }
        }

        private string _username = "";
        public string Username {
            get => string.IsNullOrEmpty(_username) ? "" : "@" + _username;
            set { _username = value; OnPropertyChanged("Username"); OnPropertyChanged("UsernameVisibility"); }
        }
        public Visibility UsernameVisibility => string.IsNullOrEmpty(_username) ? Visibility.Collapsed : Visibility.Visible;

        private BitmapImage _photo = null;
        public BitmapImage Photo {
            get => _photo;
            set { _photo = value; OnPropertyChanged("Photo"); OnPropertyChanged("NoPhotoVisibility"); }
        }

        public Visibility NoPhotoVisibility => _photo == null ? Visibility.Visible : Visibility.Collapsed;

        // Заглушка при отсутствии фото: цвет из палитры по UserId + инициалы из FullName
        public string AvatarColor => AvatarPlaceholder.GetColor(UserId);
        public string AvatarInitials => AvatarPlaceholder.GetInitials(FullName);

        private string _lastSeen = "";
        public string LastSeen {
            get => _lastSeen;
            set { _lastSeen = value; OnPropertyChanged("LastSeen"); OnPropertyChanged("LastSeenVisibility"); }
        }
        public Visibility LastSeenVisibility => string.IsNullOrEmpty(_lastSeen) ? Visibility.Collapsed : Visibility.Visible;

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
