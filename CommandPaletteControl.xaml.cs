using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;

namespace FurniturePlugin
{
    public partial class CommandPaletteControl : UserControl
    {
        public CommandPaletteControl()
        {
            InitializeComponent();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string command)
            {
                Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    // Send the command to AutoCAD
                    // The space at the end is important to execute the command immediately
                    doc.SendStringToExecute($"{command} ", true, false, false);
                }
            }
        }

        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string colorCode)
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(colorCode);
                    MainGrid.Background = new SolidColorBrush(color);

                    // Adjust text color based on background brightness
                    if (colorCode == "#FF1E1E1E") // Black/Dark
                    {
                        SetTextColor(Brushes.White);
                    }
                    else
                    {
                        SetTextColor(Brushes.Black);
                    }
                }
                catch
                {
                    // Ignore color conversion errors
                }
            }
        }

        private void SetTextColor(Brush brush)
        {
            // Recursively set text color for all TextBlocks
            // For simplicity, we'll just rely on the fact that most controls inherit foreground
            // But we might need to explicitly set it for GroupBoxes and Buttons if they have specific styles
            this.Foreground = brush;
        }
    }
}
