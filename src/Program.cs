using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("FH6 Language Switcher")]
[assembly: AssemblyDescription("Local ZIP filename switcher for Forza Horizon 6")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace HorizonLanguage
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string path = args.Length == 2 && args[0] == "--directory" ? args[1] : null;
            Application.Run(new MainForm(path));
        }
    }
}
