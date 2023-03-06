using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.IO.Ports;
using System.Threading;

namespace Cardiac_Fast_Uploader
{
    class Program
    {
        public static string TextFileName = "";
        public static string CommPortName;
        public static int iBaudRate;
        public static string version = "1.0";
        static void Main(string[] args)
        {
           //args = new string[1];
           //args[0] = "COM5";

            Console.WriteLine("GetWatchInfo Version " + version);


            Uploader upload = new Uploader();

            CommPortName = "COM1";
            iBaudRate = 921600;

            if (args.Length > 0)
            {
                CommPortName = args[0];
            }

            upload.Start( CommPortName);

            upload.sWatchId = "0000000000000000";

            if (!upload.PerformConnect())
            {
                return;
            }
            upload.bUploadActive = true;
            upload.StartTheTimer();

            // Wait for upload end
            int iTimeCntr = 0;
            while (upload.bUploadActive)
            {
                Thread.Sleep(500);
                iTimeCntr++;
                if (iTimeCntr > 30) break; // Up to 15 secons wait time
            }

            Console.WriteLine(upload.sWatchId);

            upload.performDisconnect();

            return;
        }
    }
}
