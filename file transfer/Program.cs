using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace file_transfer
{
    static class Program
    {
        public static Random Rand;

        [STAThread]
        static void Main()
        {
            Rand = new Random();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Main());
        }
    }
}
