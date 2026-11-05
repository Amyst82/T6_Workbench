using AMUI;
using System.Windows.Media;

namespace T6_Workbench
{
    public class UIColors : BaseViewModel
    {
        #region Gray shades
        private Color _gray0 = (Color)ColorConverter.ConvertFromString("#151515");
        public SolidColorBrush Gray0
        {
            get => new SolidColorBrush(_gray0);
            set => SetProperty(ref _gray0, value.Color);
        }
        public Color Gray0Color
        {
            get => _gray0;
            set => SetProperty(ref _gray0, value);
        }
        private Color _gray1 = (Color)ColorConverter.ConvertFromString("#171717");
        public SolidColorBrush Gray1
        {
            get => new SolidColorBrush(_gray1);
            set => SetProperty(ref _gray1, value.Color);
        }
        private Color Gray1Color
        {             
            get => _gray1;
            set => SetProperty(ref _gray1, value);
        }
        private Color _gray2 = (Color)ColorConverter.ConvertFromString("#1A1A1A");
        public SolidColorBrush Gray2
        {
            get => new SolidColorBrush(_gray2);
            set => SetProperty(ref _gray2, value.Color);
        }
        public Color Gray2Color
        {
            get => _gray2;
            set => SetProperty(ref _gray2, value);
        }
        private Color _gray3 = (Color)ColorConverter.ConvertFromString("#212121");
        public SolidColorBrush Gray3
        {
            get => new SolidColorBrush(_gray3);
            set => SetProperty(ref _gray3, value.Color);
        }
        public Color Gray3Color
        {
            get => _gray3;
            set => SetProperty(ref _gray3, value);
        }
        private Color _gray4 = (Color)ColorConverter.ConvertFromString("#232323");
        public SolidColorBrush Gray4
        {
            get => new SolidColorBrush(_gray4);
            set => SetProperty(ref _gray4, value.Color);
        }
        public Color Gray4Color
        {
            get => _gray4;
            set => SetProperty(ref _gray4, value);
        }
        private Color _gray5 = (Color)ColorConverter.ConvertFromString("#2F2F2F");
        public SolidColorBrush Gray5
        {
            get => new SolidColorBrush(_gray5);
            set => SetProperty(ref _gray5, value.Color);
        }
        public Color Gray5Color
        {
            get => _gray5;
            set => SetProperty(ref _gray5, value);
        }
        private Color _gray6 = (Color)ColorConverter.ConvertFromString("#808080");
        public SolidColorBrush Gray6
        {
            get => new SolidColorBrush(_gray6);
            set => SetProperty(ref _gray6, value.Color);
        }
        public Color Gray6Color
        {
            get => _gray6;
            set => SetProperty(ref _gray6, value);
        }
        #endregion


        private Color _accentColor = (Color)ColorConverter.ConvertFromString("#6659FF");
        public SolidColorBrush Accent
        {
            get => new SolidColorBrush(_accentColor);
            set => SetProperty(ref _accentColor, value.Color);
        }
        public Color AccentColor
        {
            get => _accentColor;
            set => SetProperty(ref _accentColor, value);
        }

        private SolidColorBrush _foregroundColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFAF0"));
        public SolidColorBrush Foreground
        {
            get => _foregroundColor;
            set => SetProperty(ref _foregroundColor, value);
        }
        private Color _dangerColor = (Color)ColorConverter.ConvertFromString("#FF8080");
        public SolidColorBrush Danger
        {
            get => new SolidColorBrush(_dangerColor);
            set => SetProperty(ref _dangerColor, value.Color);
        }
        public Color DangerColor
        {
            get => _dangerColor;
            set => SetProperty(ref _dangerColor, value);
        }

        private Color _warningColor = (Color)ColorConverter.ConvertFromString("#FFC74F");
        public SolidColorBrush Warning
        {
            get => new SolidColorBrush(_warningColor);
            set => SetProperty(ref _warningColor, value.Color);
        }
        public Color WarningColor
        {
            get => _warningColor;
            set => SetProperty(ref _warningColor, value);
        }

        private Color _successColor = (Color)ColorConverter.ConvertFromString("#439546");
        public SolidColorBrush Success
        {
            get => new SolidColorBrush(_successColor);
            set => SetProperty(ref _successColor, value.Color);
        }
        public Color SuccessColor
            {
            get => _successColor;
            set => SetProperty(ref _successColor, value);
        }
    }
}
