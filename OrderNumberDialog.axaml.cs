using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;

namespace DiagnoseTool
{
    public partial class OrderNumberDialog : Window
    {
        private TextBox _orderTextBox;

        public string EnteredOrderNumber => _orderTextBox?.Text ?? string.Empty;

        public OrderNumberDialog() : this(string.Empty)
        {
        }

        public OrderNumberDialog(string initialOrderNumber)
        {
            InitializeComponent();

            _orderTextBox = this.Find<TextBox>("OrderNumberTextBox");
            if (_orderTextBox != null)
            {
                _orderTextBox.Text = initialOrderNumber ?? string.Empty;
                _orderTextBox.KeyDown += OrderTextBox_KeyDown;
            }

            this.KeyDown += Window_KeyDown;

            this.Opened += (s, e) =>
            {
                if (_orderTextBox != null)
                {
                    _orderTextBox.Focus();
                    if (!string.IsNullOrEmpty(_orderTextBox.Text))
                    {
                        _orderTextBox.SelectAll();
                    }
                }
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void Header_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                this.BeginMoveDrag(e);
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
            }
        }

        private void OrderTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Confirm();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            Confirm();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Cancel();
        }

        private void Confirm()
        {
            Close(true);
        }

        private void Cancel()
        {
            Close(false);
        }
    }
}

