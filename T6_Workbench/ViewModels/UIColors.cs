using AMUI;
using System.Windows.Media;

namespace T6_Workbench
{
    public class UIColors : BaseViewModel
    {
        private Color _gray0 = (Color)ColorConverter.ConvertFromString("#151515");

        public SolidColorBrush Gray0
        {
            get => new SolidColorBrush(_gray0);
            set => Gray0Color = value.Color;
        }

        public Color Gray0Color
        {
            get => _gray0;
            set => SetColor(ref _gray0, value, nameof(Gray0), nameof(Gray0Color));
        }

        private Color _gray1 = (Color)ColorConverter.ConvertFromString("#171717");

        public SolidColorBrush Gray1
        {
            get => new SolidColorBrush(_gray1);
            set => Gray1Color = value.Color;
        }

        public Color Gray1Color
        {
            get => _gray1;
            set => SetColor(ref _gray1, value, nameof(Gray1), nameof(Gray1Color));
        }

        private Color _gray2 = (Color)ColorConverter.ConvertFromString("#1A1A1A");

        public SolidColorBrush Gray2
        {
            get => new SolidColorBrush(_gray2);
            set => Gray2Color = value.Color;
        }

        public Color Gray2Color
        {
            get => _gray2;
            set => SetColor(ref _gray2, value, nameof(Gray2), nameof(Gray2Color));
        }

        private Color _gray3 = (Color)ColorConverter.ConvertFromString("#212121");

        public SolidColorBrush Gray3
        {
            get => new SolidColorBrush(_gray3);
            set => Gray3Color = value.Color;
        }

        public Color Gray3Color
        {
            get => _gray3;
            set => SetColor(ref _gray3, value, nameof(Gray3), nameof(Gray3Color));
        }

        private Color _gray4 = (Color)ColorConverter.ConvertFromString("#232323");

        public SolidColorBrush Gray4
        {
            get => new SolidColorBrush(_gray4);
            set => Gray4Color = value.Color;
        }

        public Color Gray4Color
        {
            get => _gray4;
            set => SetColor(ref _gray4, value, nameof(Gray4), nameof(Gray4Color));
        }

        private Color _gray5 = (Color)ColorConverter.ConvertFromString("#2F2F2F");

        public SolidColorBrush Gray5
        {
            get => new SolidColorBrush(_gray5);
            set => Gray5Color = value.Color;
        }

        public Color Gray5Color
        {
            get => _gray5;
            set => SetColor(ref _gray5, value, nameof(Gray5), nameof(Gray5Color));
        }

        private Color _gray6 = (Color)ColorConverter.ConvertFromString("#808080");

        public SolidColorBrush Gray6
        {
            get => new SolidColorBrush(_gray6);
            set => Gray6Color = value.Color;
        }

        public Color Gray6Color
        {
            get => _gray6;
            set => SetColor(ref _gray6, value, nameof(Gray6), nameof(Gray6Color));
        }

        private Color _accentColor = (Color)ColorConverter.ConvertFromString("#6659FF");

        public SolidColorBrush Accent
        {
            get => new SolidColorBrush(_accentColor);
            set => AccentColor = value.Color;
        }

        public Color AccentColor
        {
            get => _accentColor;
            set => SetColor(ref _accentColor, value, nameof(Accent), nameof(AccentColor));
        }

        private Color _foregroundColor = (Color)ColorConverter.ConvertFromString("#FFFAF0");

        public SolidColorBrush Foreground
        {
            get => new SolidColorBrush(_foregroundColor);
            set => ForegroundColor = value.Color;
        }

        public Color ForegroundColor
        {
            get => _foregroundColor;
            set => SetColor(ref _foregroundColor, value, nameof(Foreground), nameof(ForegroundColor));
        }

        private Color _dangerColor = (Color)ColorConverter.ConvertFromString("#FF8080");

        public SolidColorBrush Danger
        {
            get => new SolidColorBrush(_dangerColor);
            set => DangerColor = value.Color;
        }

        public Color DangerColor
        {
            get => _dangerColor;
            set => SetColor(ref _dangerColor, value, nameof(Danger), nameof(DangerColor));
        }

        private Color _warningColor = (Color)ColorConverter.ConvertFromString("#FFC74F");

        public SolidColorBrush Warning
        {
            get => new SolidColorBrush(_warningColor);
            set => WarningColor = value.Color;
        }

        public Color WarningColor
        {
            get => _warningColor;
            set => SetColor(ref _warningColor, value, nameof(Warning), nameof(WarningColor));
        }

        private Color _successColor = (Color)ColorConverter.ConvertFromString("#439546");

        public SolidColorBrush Success
        {
            get => new SolidColorBrush(_successColor);
            set => SuccessColor = value.Color;
        }

        public Color SuccessColor
        {
            get => _successColor;
            set => SetColor(ref _successColor, value, nameof(Success), nameof(SuccessColor));
        }

        void SetColor(ref Color field, Color value, string brushProperty, string colorProperty)
        {
            if (!SetProperty(ref field, value, colorProperty))
                return;
            NotifyProperty(brushProperty);
        }
    }
}
