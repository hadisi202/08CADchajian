using System;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;

namespace FurniturePlugin
{
    public partial class CommandPalette : Window
    {
        public CommandPalette()
        {
            InitializeComponent();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string command)
            {
                // Close the window first to allow interaction with AutoCAD if needed
                // Or keep it open? Usually for a palette it might stay open, but this is a modal dialog style.
                // Let's close it for now as some commands require selection.
                // Actually, let's keep it open if it's modeless, but here we are likely showing it as Dialog or just Show.
                // If we use SendStringToExecute, it queues the command.
                
                // For better UX, we might want to close the window so the user can interact with the drawing.
                this.Close();

                Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    // Send the command to AutoCAD
                    // The space at the end is important to execute the command immediately
                    doc.SendStringToExecute($"{command} ", true, false, false);
                }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
