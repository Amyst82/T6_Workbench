using System;
using System.Windows.Input;

namespace T6_Workbench
{
    public class RelayCommand : ICommand
    {
        readonly Action<object> execute;

        readonly Predicate<object> canExecute;

        internal RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            this.execute = execute;
            this.canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) => canExecute?.Invoke(parameter) ?? true;

        public void Execute(object parameter) => execute(parameter);

        internal void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
