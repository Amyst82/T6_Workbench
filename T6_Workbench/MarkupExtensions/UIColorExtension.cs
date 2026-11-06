using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace T6_Workbench
{
    /// <summary>
    /// Binds to the application palette independently of the target's DataContext.
    /// </summary>
    public sealed class UIColorExtension : Binding
    {
        string name;

        public UIColorExtension()
        {
            Source = MainViewModel.WorkbenchViewModel;
            Mode = BindingMode.OneWay;
        }

        public UIColorExtension(string name) : this()
        {
            Name = name;
        }

        [ConstructorArgument("name")]
        public string Name
        {
            get => name;
            set
            {
                name = value;
                Path = new PropertyPath("UIColors." + value);
            }
        }
    }
}
