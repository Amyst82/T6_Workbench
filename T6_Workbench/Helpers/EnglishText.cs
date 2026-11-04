using System.Globalization;

namespace T6_Workbench
{
    /// <summary>
    /// Keeps interface and operation messages in English. This class only formats English text;
    /// it does not load language packs or change the application language at runtime.
    /// </summary>
    internal static class EnglishText
    {
        internal static string Text(string english) => english;

        internal static string Format(string englishFormat, params object[] values)
        {
            return string.Format(CultureInfo.CurrentCulture, englishFormat, values);
        }
    }
}
