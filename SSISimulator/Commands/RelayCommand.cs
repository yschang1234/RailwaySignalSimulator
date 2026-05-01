using System;
using System.Windows.Input;

namespace SSISimulator.Commands
{
    /// <summary>
    /// A simple, reusable ICommand implementation used throughout the MVVM layer.
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        /// <summary>Convenience constructor for parameterless actions.</summary>
        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute is null ? null : _ => canExecute())
        { }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        /// <summary>Forces the WPF command manager to re-evaluate CanExecute.</summary>
        public static void RaiseCanExecuteChanged() =>
            CommandManager.InvalidateRequerySuggested();
    }
}
