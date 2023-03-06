using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cardiac_Fast_Uploader
{
    class Parameters
    {
        public string sOutputFolder = "";
        public string sSelectedPort = "";
        public string sStudyID = "";
        public string sSiteID = "";

        public int iTimeBetweenGetInfo_Sec = 3;         
        public int i1stCommandTimeout_MilSec = 2500;    
        public int iRegCommandTimeout_MilSec = 550;      
        public int iPageCommandTimeout_MilSec = 650;
        public int iBlockCommandTimeout_MilSec = 1500; 
        public int iNumSendCommandResend = 5;
        public int iPageRequestRetry = 5;  
        public int iBlockSizeInPages = 128;  
        public bool bStatistics = false;
        public bool bDebug = false; 
        public bool bParseDuringUpload = false; 
        public double dAutoRecoverFailDelay = 1.0;  
        public static int iMinCSVfileGap = 100;  
        private string datafile = "parametersFstInfo.txt";
        private string sExpectedTitle = "Watch-FastUploader-Parameters";

        public void SetValues()
        {
           
        }
    }
}
