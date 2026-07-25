using System;
using System.Linq;

namespace TelegramWP10
{
    // Кружок-заглушка для аватарки без фото: цвет из палитры (детерминированный
    // по id — тот же собеседник всегда получает тот же цвет, как в оригинальном
    // Telegram) + инициалы из имени.
    internal static class AvatarPlaceholder
    {
        private static readonly string[] Colors = new[]
        {
            "#E17076", // красный
            "#EDA86C", // оранжевый
            "#A695E7", // фиолетовый
            "#7BC862", // зелёный
            "#6EC9CB", // бирюзовый
            "#65AADD", // синий
            "#EE7AAE", // розовый
        };

        public static string GetColor(long id)
        {
            long a = id < 0 ? -id : id;
            return Colors[(int)(a % Colors.Length)];
        }

        public static string GetInitials(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";

            var words = title.Trim()
                .Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 0)
                .ToArray();

            if (words.Length == 0) return "";

            string result = char.ToUpperInvariant(words[0][0]).ToString();
            if (words.Length > 1)
                result += char.ToUpperInvariant(words[1][0]);

            return result;
        }
    }
}
