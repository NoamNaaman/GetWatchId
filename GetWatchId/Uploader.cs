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
    public enum MssgFail
    {
        TooShort = 0, WrongSize = 1, WrongCRC = 2, ZeroCRC = 3, RecogniseBytes = 4,
        PageId = 5, SaveFileErr = 6, WrongOcode = 7, NotFailed = 255
    }

    public enum MachineState
    {
        NoChange = 0, Initialization = 1, Before = 2, Connected = 3, ConnectedPlas = 4, GetParams = 5, ReadyForActivity = 6,
        StartUpload = 7, BlockUpload = 8, PageUpload = 9, SetTime = 10, EraseWatchFlash = 11, UploadUserStopped = 12,
        UploadErrorStopped = 13, UploadContinueOrStop = 14, UploadRecovery = 15, ContinueBlockUpload = 16, OnParse = 17, DebugMode = 55
    }

    class Uploader
    {
        public Parameters ParamClss;
        public static string sw_version = "2.4";

        static MachineState ExpectedMachineStatus;
        static public int i2sec;

        // State machine variables
        static public long lTime2Wait;
        static public long lTimeStart;
        // Time out variables
        static public long lMaxTimeOut;
        static public bool bWaitTimeOut = false;
        static public UInt16 iNumRetrys;

        private string[] lPorts;

        private MachineState status;
    
        private Protocol.Opcode WaitOpcode;

        public const string WatchNotSet = "No Watch";
        static public string spWatchId;
        static public string spWatchIdX;
        static public string sYearOfBirth = "UnKnown";
        static public string sYearOfBirthX = "UnKnown";

        static public int iExpectedRecievedMessageSize;
        static public string sFullBasicOutputFileName;

        static public string sUploadFileNameExtension = ".dat";

        static public bool bForceFileUploadStop = false;

        // Handle file data 
        public static UInt32 Numbers_Of_Pages_In_Block = 64;
        public const UInt32 BYTES_IN_PAGE = 256;
        public const int FULL_PAGE_MESSAGE_SIZE = 273;                                   // 9 for header + 4 + 4 + 256 data

        //static public bool bUploadExpected;
        static public int iBlockId;
        static public UInt32 uUploadNumberOfPages;
        static public UInt32 uUploadNumberOfBlocks;
        static public UInt32 uUploadBlockFirstPage;
        static public UInt32 uUploadBlockLastPage;
        static public UInt32 uUploadCurrPageIndex;
        static public UInt32 uUploadSuccessPageIndex;
        static public int iUploadCurrInternalPageIndex;
        public static int iNumberOfPagesReadInBlock;

        static public UInt32 uCurrBlockNumOfPages;
        static public UInt32 uNumberOfPagesInLastBlock;

        static public bool bExpectedLongData;
        public static int rcvsize = 0;
        public static bool bPageExpected = false;
        public static byte[] rxDataIn = new byte[1024 * 100];
        public static byte[] rxBlockData;
        public static byte[] rxPageData = new byte[FULL_PAGE_MESSAGE_SIZE * 3];
        public static byte[] rxPageArray = new byte[FULL_PAGE_MESSAGE_SIZE];

        //        private static bool bExpectedPage = false;
        private static bool bUploadFailed;

        private static int iPageRequestRetry;
        private static string sUploadStatus;
        private static bool bUploadEnd = false;
        public static UInt32 uBlockStartPage;
        
        public int iZeroSendCVntr;
        private bool bWatchConnected;

        MachineState statusBeforParse = MachineState.NoChange;
        private bool bParserStarted;
        private bool bParserStartedManualy = false;

        // public bool bDuringUpload = false;   Obselete mechanism
        public bool bForceNewMachineState;

        // Statistics
        private int iNumMssgs;
        private int iNumBadMssgs;
        public static long lDownloadTimeStart;
        public static long lDownloadTimeStop;

        // Debuging tools   - start
        public static bool bDebug = false;
        static public byte crc0;
        static public byte crc1;
        static public bool bBlock;

        public bool bUploadActive;
        public string sWatchId;
        public int iUploadEndStatus ;
        private System.Timers.Timer timerMain;
        static SerialPort commPort;

        //===========================================================================================
        // Name:    Start
        // Title:   Starts the upload class
        // Version: 1.0 - Eyal Shomrony
        //===========================================================================================
        public void Start(string commPortName)
        {
            ParamClss = new Parameters();
            ParamClss.sSelectedPort = commPortName;

            i2sec = 1000 / 1;

            SetSatusEenvironments(MachineState.Initialization);

            ExpectedMachineStatus = MachineState.NoChange;

            WaitOpcode = Protocol.Opcode.OpcodeZero; 

            spWatchId = spWatchIdX = WatchNotSet;

            iNumMssgs = iNumBadMssgs = 0;

            bParserStarted = false;

            bDebug = ParamClss.bDebug;

            Numbers_Of_Pages_In_Block = (UInt32)ParamClss.iBlockSizeInPages;
            rxBlockData = new byte[1024 * Numbers_Of_Pages_In_Block];
            bWatchConnected = false;
        }

        //===========================================================================================
        // Name:    StartTheTimer
        // Title:   Sets and starts the timer
        // Details: Activated 1/1000 of a second, and is responsible for performing all 
        //          stages of communication with the watch. 
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        public void StartTheTimer()
        {
            timerMain = new System.Timers.Timer(1);
            i2sec = 1000;
            timerMain.Elapsed += timerMain_Tick; // new ElapsedEventHandler(OnTimedEvent);
            timerMain.Interval = 1;
            timerMain.AutoReset = true;
            timerMain.Enabled = true;
        }

        //===========================================================================================
        // Name:    timerMain_Tick
        // Title:   Main timer activity, called by the timer event, and execute the expected
        //          time activity depending on the working state
        // Details: Called 1/1000 of a second, and is responsible for performing all 
        //          stages of communication with the watch.   
        //          Contains the Timoout control mechanism at the beginning
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        private void timerMain_Tick(object sender, EventArgs e)
        {
            if (bParserStarted) status = MachineState.OnParse;

            if (bForceNewMachineState)
            {
                SetSatusEenvironments(ExpectedMachineStatus);
                bForceNewMachineState = false;
                return;
            }

            if (bUploadEnd)
            {
                HandleUploadEnd();
            }

            // Update State Machine GUI environment and timing in case the machine state had been changed
            // (exclude when this change is expected to be ignored and only effect the state machine)
            if (ExpectedMachineStatus != MachineState.NoChange)
            {
                SetSatusEenvironments(ExpectedMachineStatus);
                ExpectedMachineStatus = MachineState.NoChange;
            }

            // Check if waiting for message back
            //----------------------------------------------------------
            if (WaitOpcode != Protocol.Opcode.OpcodeZero)
            {
                // Timoout control mechanism
                // ============================
                if (bWaitTimeOut)
                {
                    //timerMain.Stop();
                    timerMain.Enabled = false;
                    long millisecondsNow = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    long msGap = millisecondsNow - lTimeStart;

                    if (lMaxTimeOut <= msGap) // Timeout 
                    {
                        // More resend is required
                        if (iNumRetrys < ParamClss.iNumSendCommandResend)
                        {
                            Protocol.SendMessageAgain();
                            lTimeStart = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                            iNumRetrys++;
                        }
                        // Too many timeouts - stop the proccess
                        else
                        {
                            bWaitTimeOut = false;
                            WaitOpcode = Protocol.Opcode.OpcodeZero;
                            performDisconnect();
                            SetSatusEenvironments(MachineState.Before);
                            Console.WriteLine("Time-out detected");
                            //MessageBox.Show("Time-out detected");
                        }

                        if (Protocol.ErrMssg.Length > 0)
                        {
                            bWaitTimeOut = false;
                            WaitOpcode = Protocol.Opcode.OpcodeZero;
                            performDisconnect();
                            SetSatusEenvironments(MachineState.Before);
                            Console.WriteLine("Communication Error: " + Protocol.ErrMssg);
                            //MessageBox.Show("Communication Error: " + Protocol.ErrMssg);
                        }
                    }
                    //timerMain.Start(); 
                    timerMain.Enabled = true;
                }
                return;
            }
            else
            {
                if (spWatchId != WatchNotSet && spWatchId != spWatchIdX) // New watch detected
                {
                    bWatchConnected = true;
                    spWatchIdX = spWatchId;
                    //SetSatusEenvironments(MachineState.GetParams);
                    //Console.WriteLine("Watch id = " + spWatchId);
                }

                if (sYearOfBirth != "UnKnown" && sYearOfBirth != sYearOfBirthX)
                {
                    sYearOfBirthX = sYearOfBirth;

                    // On line - Instead start uploading 
                    //============================================
                    Console.WriteLine("Year of birth = " + sYearOfBirth);

                    DateTime aCurrDate = DateTime.Now;
                    string date = aCurrDate.ToString("ddMMyy_hhmmss");

                    bUploadEnd = false;
                    bUploadFailed = false;

                    // Count the total time of uploading from the reqauest of flush size till upload end
                    lDownloadTimeStart = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    lDownloadTimeStop = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                    bParserStartedManualy = true;

                    //SetSatusEenvironments(MachineState.StartUpload);
                }
            }

            // No Message back is required, the regular state machine activities
            //=====================================================================

            // Check if time to execute next activity
            long lsCurr = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (lsCurr - lTimeStart < lTime2Wait) return;

            // timerMain.Stop(); 
            timerMain.Enabled = false;

            lTimeStart = lsCurr;
            bWaitTimeOut = false;
            byte[] txbuff = null;

            switch (status)
            {
                default:
                // Find the serial ports list to initializes the GUI list of ports
                //===================================================================
                case MachineState.Initialization:
                    // lPorts = System.IO.Ports.SerialPort.GetPortNames();
                    /* comboBoxPorts.Items.Clear();

                    for (int i = 0; i < lPorts.Length; i++)
                    {
                        comboBoxPorts.Items.Add(lPorts[i]);
                    }

                    if (ParamClss.sSelectedPort != "none")
                    {
                        for (int i = 0; i < comboBoxPorts.Items.Count; i++)

                            if (comboBoxPorts.Items[i].ToString() == ParamClss.sSelectedPort)
                            {
                                comboBoxPorts.SelectedIndex = i;
                                buttonConnect.Show();
                                break;
                            }
                    } */

                    SetSatusEenvironments(MachineState.Before);
                    break;
                // Idle mode till watch is connected
                //===================================================================
                case MachineState.Before:
                    lTime2Wait = 1;
                    break;

                // Sending Zero for weake-up and GetInfo for start
                //===================================================================
                case MachineState.Connected:
                    txbuff = new byte[101];
                    for (byte ii = 0; ii < 100; ii++) txbuff[ii] = ii;
                    SendMessageToWatch(Protocol.DevType.DevTypeWatchFE, Protocol.Opcode.OpcodeZero, txbuff, 100,
                                                            ParamClss.iRegCommandTimeout_MilSec);
                    SetSatusEenvironments(MachineState.ConnectedPlas);
                    break;
                // Sending OpcodePC_GetInfo for start to get Watch-Id
                //===================================================================
                case MachineState.ConnectedPlas:
                    SendMessageToWatch(Protocol.DevType.DevTypeWatchFE, Protocol.Opcode.OpcodePC_GetInfo, txbuff, 0,
                                                            ParamClss.iRegCommandTimeout_MilSec * 2);
                    break;
                // Sending OpcodeGetParam for start to get Watch-Id
                //===================================================================
                case MachineState.GetParams:
                    SendMessageToWatch(Protocol.DevType.DevTypeWatchFE, Protocol.Opcode.OpcodeGetParam, txbuff, 0,
                                                            ParamClss.iRegCommandTimeout_MilSec * 2);
                    break;
                // Operate during the waiting for Operation sending OpcodePC_GetInfo
                //===================================================================
                case MachineState.ReadyForActivity:
                    SendMessageToWatch(Protocol.DevType.DevTypeWatchFE, Protocol.Opcode.OpcodePC_GetInfo, txbuff, 0,
                                                            ParamClss.iRegCommandTimeout_MilSec);
                    break;
                // Operate while waiting for the operator's decision on whether to 
                // continue update or quit it ( return to normal standby )
                //===================================================================
                case MachineState.UploadContinueOrStop:
                    SendMessageToWatch(Protocol.DevType.DevTypeWatchFE, Protocol.Opcode.OpcodePC_GetInfo, txbuff, 0,
                                                            ParamClss.iRegCommandTimeout_MilSec);
                    break;

                // Sending the set date and time command
                //===================================================================
                case MachineState.SetTime:
                    DateTime aCurrDate = DateTime.Now;
                    txbuff = new byte[6];
                    int iyr = aCurrDate.Year - 2000;
                    txbuff[0] = (byte)iyr;
                    txbuff[1] = (byte)aCurrDate.Month;
                    txbuff[2] = (byte)aCurrDate.Day;
                    txbuff[3] = (byte)aCurrDate.Hour;
                    txbuff[4] = (byte)aCurrDate.Minute;
                    txbuff[5] = (byte)aCurrDate.Second;
                    SendMessageToWatch(Protocol.DevType.DevTypeWatchFE, Protocol.Opcode.OpcodeSetDateTime, txbuff, 6,
                                                                                ParamClss.iRegCommandTimeout_MilSec);
                    break;

                // Sending the flush erase command
                //===================================================================
                case MachineState.EraseWatchFlash:
                    txbuff = new byte[4];
                    txbuff[0] = 0x1A;
                    txbuff[1] = 0x2B;
                    txbuff[2] = 0x4F;
                    txbuff[3] = 0x6E;
                    SendMessageToWatch(Protocol.DevType.DevTypeWatchFE, Protocol.Opcode.OpcodeNorFlashErase, txbuff, 4,
                                                                                ParamClss.iPageCommandTimeout_MilSec);
                    break;

                // Starts the uploading proccess - Ask for flash data size
                //===================================================================
                case MachineState.StartUpload:
                    SendMessageToWatch(Protocol.DevType.DevTypeWatchFE, Protocol.Opcode.OpcodeGetNorFlashDataSize, txbuff, 0,
                                                                                ParamClss.iRegCommandTimeout_MilSec);
                    //textBoxStatus.Text = sUploadStatus = "Upload active";
                    sUploadStatus = "Upload active"; //<<<>>>
                    break;

                // Handle uploading suspending by error
                //----------------------------------------------------------
                case MachineState.UploadErrorStopped:
                    SetSatusEenvironments(MachineState.UploadRecovery);
                    break;

                // Handle uploading suspending by user request
                //----------------------------------------------------------
                case MachineState.UploadUserStopped:
                    //textBoxStatus.Text = sUploadStatus;
                    break;
                // Debug only
                //---------------------------------------------------------------------
                case MachineState.DebugMode:
                    SendMessageToWatch(Protocol.DevType.DevTypeWatchFE, Protocol.Opcode.OpcodeDebug, txbuff, 0,
                                                                                                ParamClss.iBlockCommandTimeout_MilSec);
                    break;

                // upload page when a defected page uploaded during block uploar 
                //--------------------------------------------------------------------
                case MachineState.PageUpload:
                    //labelDounloadTime.Text = string.Format("Page {0}", uUploadCurrPageIndex);
                    Console.Write((char)13 + string.Format("Page {0}", uUploadCurrPageIndex));
                    byte[] bOffset = BitConverter.GetBytes(uUploadCurrPageIndex);
                    SendMessageToWatch(Protocol.DevType.DevTypeWatchFE, Protocol.Opcode.OpcodeGetNorFlashPage, bOffset, 4,
                                                                                                ParamClss.iPageCommandTimeout_MilSec);
                    break;
                // Contnue the uloading activity from its last point
                //------------------------------------------------------
                case MachineState.UploadRecovery:
                    //labelDounloadTime.Text = string.Format("Block Rec {0}", iBlockId);
                    Console.Write((char)13 + string.Format("Block Rec {0}", iBlockId+1));

                    // The block fail during first page quit this spep and continue regulary
                    if (iNumberOfPagesReadInBlock > 0)
                    {
                        uCurrBlockNumOfPages = uCurrBlockNumOfPages - (UInt32)iNumberOfPagesReadInBlock;
                        uBlockStartPage = uUploadSuccessPageIndex + 1;
                        txbuff = BuildBlockCommandParameters();
                        iNumberOfPagesReadInBlock = 0;
                        SendMessageToWatch(Protocol.DevType.DevTypeWatchFE, Protocol.Opcode.OpcodeGetFlash16kbBlock, txbuff, 8,
                                                                                                ParamClss.iBlockCommandTimeout_MilSec);
                    }
                    SetSatusEenvironments(MachineState.BlockUpload);
                    break;

                // Upload blocks one after the other 
                //------------------------------------------------------
                case MachineState.ContinueBlockUpload:
                    //bExpectedPage = false;
                    // bDuringUpload = true;  Obselete mechanism
                    HandleBlock();
                    break;
                case MachineState.BlockUpload:
                    break;
                case MachineState.OnParse:
                    break;
            }

            //timerMain.Start(); <<<>>> Point
            timerMain.Enabled = true;
        }

        //===========================================================================================
        // Name:    HandleUploadEnd
        // Title:   Handle the upload end
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        private void HandleUploadEnd()
        {
        }

        //===========================================================================================
        // Name:    chain2arrays
        // Title:   Chain the two input arrays
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        private byte[] chain2arrays(byte[] bArr1, byte[] bArr2)
        {
            int isize1 = bArr1.Length;
            int isize2 = bArr2.Length;
            int iSizeOut = isize1 + isize2;
            byte[] aArr_out = new byte[iSizeOut];
            int index = 0;
            for (int i = 0; i < isize1; i++, index++) aArr_out[index] = bArr1[i];
            for (int i = 0; i < isize1; i++, index++) aArr_out[index] = bArr2[i];
            return aArr_out;
        }

        //===========================================================================================
        // Name:    SendMessageToWatch
        // Title:   Send a message to the watch and get ready for watch response message
        // Details: 1) Starts the timeout mechanism, which allows you to wait a requested number of 
        //             seconds for a response-message. It also allows multiple repetitions of sending  
        //             the message when there is a failure to receive a response message
        //          2) Starts the correctness mechanism of the response-message
        //          3) Sends the message
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        private void SendMessageToWatch(Protocol.DevType dst, Protocol.Opcode opcode, byte[] data, UInt16 len, int timeOutIn_mSecond)
        {
            //===============================================
            // 1) Start the time-out mechanism
            //===============================================
            bWaitTimeOut = true;
            if (data == null) data = new byte[100];

            lMaxTimeOut = timeOutIn_mSecond;
            WaitOpcode = opcode;
            iNumRetrys = 0;

            //===============================================
            // 2) Start the answer message legality checking 
            //===============================================
            bExpectedLongData = false;
            iExpectedRecievedMessageSize = -1;

            // Calculate the expected recieved message lenght
            switch (opcode)
            {
                //--------------------------------------------------------------------------------------------
                // (1,Eight Watch ID bytes followed by irrelevant additional information)
                //--------------------------------------------------------------------------------------------
                case Protocol.Opcode.OpcodePC_GetInfo:
                    WaitOpcode = Protocol.Opcode.OpcodeGetInfo;
                    break;

                case Protocol.Opcode.OpcodeZero:
                default:
                    break;
                //--------------------------------------------------------------------------------------------
                // (13,32 bytes of information the 11th is year of birth 
                //--------------------------------------------------------------------------------------------
                case Protocol.Opcode.OpcodeGetParam:
                    iExpectedRecievedMessageSize = Protocol.PROTOCOL_HEADER_SIZE + 32;
                    break;
                //--------------------------------------------------------------------------------------------
                // (8,size[4])
                //--------------------------------------------------------------------------------------------
                case Protocol.Opcode.OpcodeGetNorFlashDataSize:
                    iExpectedRecievedMessageSize = Protocol.PROTOCOL_HEADER_SIZE + len + 4; // Header + file-name + 4bytes (file length)
                    break;

                //--------------------------------------------------------------------------------------------
                // Send 68,start-page[4],num-pages[4] 
                // recieve multi  (9,pageNo[4],data[256]) num of pages
                //--------------------------------------------------------------------------------------------
                case Protocol.Opcode.OpcodeGetFlash16kbBlock:
                    WaitOpcode = Protocol.Opcode.OpcodeGetNorFlashPage;
                    iExpectedRecievedMessageSize = (int)uCurrBlockNumOfPages * FULL_PAGE_MESSAGE_SIZE; // (Protocol.PROTOCOL_HEADER_SIZE + 8 + 256)// header + 4 + 4 + 256
                    uUploadCurrPageIndex = uUploadBlockFirstPage;
                    iUploadCurrInternalPageIndex = 0;
                    rcvsize = 0;
                    bExpectedLongData = true;
                    //bUploadExpected = true;
                    bPageExpected = false;
                    // bDuringUpload = true; // Obselete mechanism
                    break;
                //--------------------------------------------------------------------------------------------
                // Send 200 
                // return 1024 times 1
                //--------------------------------------------------------------------------------------------
                case Protocol.Opcode.OpcodeDebug:
                    iExpectedRecievedMessageSize = 1024;
                    rcvsize = 0;
                    bExpectedLongData = true;
                    bPageExpected = false;
                    break;
                //--------------------------------------------------------------------------------------------
                // Send 9,page[4]
                // recieve multi  (9,pageNo[4],data[256]) 
                //--------------------------------------------------------------------------------------------
                case Protocol.Opcode.OpcodeGetNorFlashPage:
                    iExpectedRecievedMessageSize = FULL_PAGE_MESSAGE_SIZE;   // (Protocol.PROTOCOL_HEADER_SIZE + 8 + 256)// header + 4 + 4 + 256
                    rcvsize = 0;
                    bExpectedLongData = true;
                    //bUploadExpected = true;
                    bPageExpected = true;
                    break;

                //--------------------------------------------------------------------------------------------
                // Send 69,date[6]  (yy,MM,dd,hh,mm,ss)
                // recieve multi  69
                //--------------------------------------------------------------------------------------------
                case Protocol.Opcode.OpcodeSetDateTime:
                case Protocol.Opcode.OpcodeNorFlashErase:
                    iExpectedRecievedMessageSize = Protocol.PROTOCOL_HEADER_SIZE;
                    break;
            }

            //===============================================
            // 3) Send the message
            //===============================================
            lTimeStart = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            Protocol.SendMessage(dst, opcode, data, len);
        }

        //===========================================================================================
        // Name:    SetSatusEenvironments
        // Title:   Arrange the GUI and time environment to suit the needs of the working state
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        private void SetSatusEenvironments(MachineState currStatus)
        {
            //  bDuringUpload = false;  // Obselete mechanism
            bForceNewMachineState = false;
            if (currStatus == MachineState.OnParse)
            {
                lTimeStart = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                status = currStatus;

                lTime2Wait = 1;
                lTime2Wait = 25;
                iZeroSendCVntr = 0;

            }

            else if (currStatus == MachineState.NoChange)
            {
                return;
            }

            lTimeStart = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            status = currStatus;

            lTime2Wait = 1;

            switch (currStatus)
            {
                // Initial state
                default:
                case MachineState.Initialization:
                case MachineState.Before:
                    break;
                case MachineState.ConnectedPlas:
                    lTime2Wait = ParamClss.i1stCommandTimeout_MilSec;
                    break;
                case MachineState.GetParams:
                    lTime2Wait = 2;
                    break;
                case MachineState.Connected:
                    break;
                case MachineState.ReadyForActivity:
                    lTime2Wait = ParamClss.iTimeBetweenGetInfo_Sec * i2sec;  // Once in 3 seconds ask for watch id to ensure connecting mode
                    break;
                case MachineState.StartUpload:
                   break;
                case MachineState.BlockUpload:
                case MachineState.ContinueBlockUpload:
                case MachineState.UploadRecovery:
                case MachineState.PageUpload:
                    lTime2Wait = 2;
                    break;
                case MachineState.EraseWatchFlash:
                case MachineState.SetTime:
                    break;
                case MachineState.UploadErrorStopped:
                    Console.WriteLine( "Upload stopped by Error");
                    bWaitTimeOut = false;
                    lTime2Wait = (int)(ParamClss.dAutoRecoverFailDelay * (double)i2sec);
                    break;
                case MachineState.UploadContinueOrStop:
                case MachineState.UploadUserStopped:
                    bWaitTimeOut = false;
                    if (currStatus == MachineState.UploadUserStopped)
                        Console.WriteLine("Upload stopped by request");
                    lTime2Wait = 1;
                    break;

                case MachineState.DebugMode:
                    break;
                case MachineState.OnParse:
                    lTime2Wait = 25;
                    iZeroSendCVntr = 0;
                    break;
            }
        }

        //===========================================================================================
        // Name:    PerformConnect
        // Title:   Connect the selected serial port
        // Version: 1.0 - Eyal Shomrony
        //===========================================================================================
        public bool PerformConnect()
        {           

            // Connect
            if (ParamClss.sSelectedPort == "")
            {
                Console.WriteLine(string.Format("Port must be selected"));
                return false;
            }

            commPort = new System.IO.Ports.SerialPort();
            commPort.PortName = ParamClss.sSelectedPort;
            commPort.BaudRate = 921600;
            commPort.ParityReplace = ((byte)(0));
            commPort.ReadBufferSize = 35000;
            commPort.ReadTimeout = 50;
            commPort.WriteTimeout = 25;
            commPort.ErrorReceived += new System.IO.Ports.SerialErrorReceivedEventHandler(this.commPort_ErrorReceived);
            commPort.DataReceived += new System.IO.Ports.SerialDataReceivedEventHandler(this.commPort_DataReceived);

            if (commPort.IsOpen == false)
            {
                try
                {
                    commPort.Open();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Port connection failed " + ex.Data.ToString());
                    return false;
                }

                Protocol.SetPort(commPort);

                // Start the state machine activities: 
                // MachineState.Connected)          Send Zero
                // MachineState.ConnectedPlas)      Send GetInfo    (recieve spWatchId)
                // MachineState.GetParams)          Send GetParam   (recieve sYearOfBirth)
                SetSatusEenvironments(MachineState.Connected);
            }
            return true;

        }

        //===========================================================================================
        // Name:    performDisconnect
        // Title:   Performs the serial port disconnect activities
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        public void performDisconnect()
        {
            try
            {
                commPort.Close();
            }
            catch (Exception ex)
            {
                string msg = "Port disconnection failed " + ex.Data.ToString();
                Console.WriteLine(msg);
                return;
            }

            bWatchConnected = false;

            spWatchId = WatchNotSet;           
            sYearOfBirth = "UnKnown";
            Protocol.SetPort(null);

            SetSatusEenvironments(MachineState.Before);
        }

        //===========================================================================================
        // Name:    ConfirmMessage
        // Title:   Performs standard activity when you receive a valid message that the system 
        //          expects to receive
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        private void ConfirmMessage(MachineState stt, bool bStopLong = false)
        {
            WaitOpcode = Protocol.Opcode.OpcodeZero; // Not expect any message
            bWaitTimeOut = false;
            ExpectedMachineStatus = stt;
            if (bStopLong)
            {
                // bDuringUpload = false;  // Obselete mechanism
                bForceNewMachineState = true;
            }
        }

        //===========================================================================================
        // Name:    commPort_DataReceived
        // Title:   Receives the data from the selected opend comm port
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        private void commPort_DataReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
        {
            // Long answer
            if (bExpectedLongData)
            {
                //bDuringUpload = true;

                int readBytes = commPort.BytesToRead;
                if (readBytes < 1) return;

                try
                {
                    commPort.Read(rxDataIn, 0, readBytes);
                    //if (!bUploadExpected) return;

                    // Single page handling
                    if (bPageExpected)
                    {
                        //  Copy the read data into the working buffer start at
                        //  the previus read offset - for packge received in parts (usb bug)

                        //for (int i = 0; i < readBytes; i++) rxPageData[i + rcvsize] = rxDataIn[i];
                        Array.Copy(rxDataIn, 0, rxPageData, rcvsize, readBytes);
                        rcvsize += readBytes;

                        if (rcvsize >= FULL_PAGE_MESSAGE_SIZE)
                        {
                            //bUploadExpected = false;
                            rcvsize = 0;
                            HandlePage();
                            return;
                        }
                    }
                    // Block handling
                    else
                    {
                        //  Copy the read data into the working buffer start at
                        //  the previus read offset - for packge received in parts (usb bug)
                        Array.Copy(rxDataIn, 0, rxBlockData, rcvsize, readBytes);

                        //for (int i = 0; i < readBytes; i++) rxBlockData[i + rcvsize] = rxDataIn[i];

                        rcvsize += readBytes;

                        if (rcvsize >= iExpectedRecievedMessageSize)
                        {
                            //bUploadExpected = false;                          
                            rcvsize = 0;
                            HandleBlock();
                            return;
                        }


                    }
                }
                catch (Exception ex)
                {
                    //rcvsize = 0;
                    string err = ex.ToString();
                }
            }
            // Regular answer
            else
            {
                int count = commPort.BytesToRead;
                byte[] ByteArray = new byte[count];
                commPort.Read(ByteArray, 0, count);

                // Message is expected
                if (WaitOpcode != Protocol.Opcode.OpcodeZero)
                {
                    if (HandleMessage(ByteArray, count) != MssgFail.NotFailed) iNumBadMssgs++;
                }
            }
        }

        private void commPort_ErrorReceived(object sender, System.IO.Ports.SerialErrorReceivedEventArgs e)
        {

        }

        //===========================================================================================
        // Name:    HandleBlock
        // Title:   Handle the input block 
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        private void HandleBlock()
        {
        }

        //===========================================================================================
        // Name:    HandlePage
        // Title:   Handle the input page 
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        private void HandlePage()
        {
        }

        //===========================================================================================
        // Name:    HandleMessage
        // Title:   Handle the input message (from the watch)
        // Details: 1) Check if the message size is at least standard message header size
        //          2) Check if  the message size feets the expected message size
        //          3) Check message header & crc
        //          4) Parse the message information and procees end of message activities
        // Version: V1.0- Eyal shomrony
        //===========================================================================================
        private MssgFail HandleMessage(byte[] ByteArray, int count, bool bSizeCheck = true)
        {
            iNumMssgs++;
            //======================================================================================
            // 1) Check if the message size is at least standard message header size
            //======================================================================================
            if (count < Protocol.PROTOCOL_HEADER_SIZE)
            {
                return MssgFail.TooShort;
            }

            //======================================================================================
            // 2) Check if  the message size feets the expected message size
            //======================================================================================
            if (bSizeCheck && (iExpectedRecievedMessageSize != -1 && count < iExpectedRecievedMessageSize))
            {
                return MssgFail.WrongSize;
            }

            //======================================================================================
            // 3) Check message header & crc
            //======================================================================================
            if (ByteArray[Protocol.PREAMBLE_OFFSET] == 171 && ByteArray[Protocol.PREAMBLE_OFFSET + 1] == 248)
            {
                // Check CRC legality
                //-----------------------------
                int datalen = 0;
                datalen = (int)ByteArray[Protocol.LENGTH_OFFSET + 1]; // MS
                datalen = datalen << 8;
                datalen += (int)ByteArray[Protocol.LENGTH_OFFSET]; // LS
                crc0 = ByteArray[Protocol.CRC_OFFSET];
                crc1 = ByteArray[Protocol.CRC_OFFSET + 1];
                bool bCrcZero = false;
                if (crc0 == 0 && crc1 == 0) bCrcZero = true;

                ByteArray[Protocol.CRC_OFFSET] = ByteArray[Protocol.CRC_OFFSET + 1] = 0;
                UInt16 crc = Protocol.ProtocolCalcCrc(ByteArray, (UInt16)count);
                byte[] byteArray = BitConverter.GetBytes(crc);
                if (byteArray[0] == crc0 && byteArray[1] == crc1)
                {
                    // Find the message Opcode
                    Protocol.Opcode currOp = (Protocol.Opcode)ByteArray[Protocol.OPCODE_OFFSET];

                    // Continue only when received opcode is the expected opcode                    
                    if (currOp != WaitOpcode)
                    {
                        return MssgFail.WrongOcode;
                    }

                    // For Commands text box
                    long millisecondsNow = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                    lTimeStart = millisecondsNow;

                    //======================================================================================
                    // 4) Parse the message information and procees end of message activities
                    //======================================================================================
                    switch (currOp)
                    {
                        //--------------------------------------------------------------------------------------------
                        // Handle message that is not expected for this communication definition
                        //--------------------------------------------------------------------------------------------
                        default:
                            break;
                        //--------------------------------------------------------------------------------------------
                        // Handle Get Info (1,Eight Watch ID bytes followed by irrelevant additional information)
                        //--------------------------------------------------------------------------------------------
                        case Protocol.Opcode.OpcodePC_GetInfo:
                        case Protocol.Opcode.OpcodeGetInfo:
                            byte[] watch_id = new byte[Protocol.WATCHID_LENGTH];
                            UInt64 watchid = 0;
                            UInt64 multiply = 1;
                            Array.Copy(ByteArray, Protocol.DATA_OFFSET + Protocol.WATCHID_OFFSET, watch_id, 0, Protocol.WATCHID_LENGTH);
                            for (int i = Protocol.WATCHID_LENGTH; i > 0; i--)
                            {
                                watchid += multiply * (UInt64)ByteArray[Protocol.DATA_OFFSET + Protocol.WATCHID_OFFSET + (i - 1)];
                                multiply = multiply << 8;
                            }
                            spWatchId = watchid.ToString("X");
                            sWatchId = spWatchId;
                            bUploadActive = false;

                            ConfirmMessage(MachineState.Before); // END

                            break;

                        //--------------------------------------------------------------------------------------------
                        // Handle the get parametrs to get the year of birth
                        //--------------------------------------------------------------------------------------------
                        case Protocol.Opcode.OpcodeGetParam:
                            byte bYear = ByteArray[Protocol.DATA_OFFSET + 11];
                            int iYear = 1900 + (int)bYear;
                            sYearOfBirth = iYear.ToString();
                            ConfirmMessage(MachineState.ReadyForActivity);
                            break;
                        //--------------------------------------------------------------------------------------------
                        // Handle Open Files (63,"file-name",len=uint32)
                        //--------------------------------------------------------------------------------------------
                        case Protocol.Opcode.OpcodeGetNorFlashDataSize:
                            break;

                        //--------------------------------------------------------------------------------------------
                        // Handle get page(s) (9,pageId[4],data[256]) | during upload proccess, called by block handler
                        // or page handler
                        //--------------------------------------------------------------------------------------------
                        case Protocol.Opcode.OpcodeGetNorFlashPage:
                             break;

                        //--------------------------------------------------------------------------------------------
                        // Handle Sent date and time
                        //--------------------------------------------------------------------------------------------
                        case Protocol.Opcode.OpcodeSetDateTime:
                        case Protocol.Opcode.OpcodeNorFlashErase:
                            ConfirmMessage(MachineState.ReadyForActivity);
                            break;
                    }
                }
                else
                {
                    if (bCrcZero) return MssgFail.ZeroCRC;
                    else return MssgFail.WrongCRC; // wrong CRC 
                }
            }
            else
            {
                return MssgFail.RecogniseBytes; // wrong message start recognision
            }

            return MssgFail.NotFailed;
        }

        //===========================================================================================
        // Name:    BuildBlockCommandParameters
        // Title:   Builds the upload block command parameters (start & num of pages)
        // Details: The input 4 butes are arranged in reverse order byte[0] is the ls byte[3] is
        //          the ms.
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        private byte[] BuildBlockCommandParameters()
        {
            uUploadBlockFirstPage = uBlockStartPage;
            uUploadBlockLastPage = uBlockStartPage + uCurrBlockNumOfPages - 1;

            byte[] bStart = BitConverter.GetBytes(uBlockStartPage);
            byte[] bSize = BitConverter.GetBytes(uCurrBlockNumOfPages);
            return chain2arrays(bStart, bSize);
        }

        //===========================================================================================
        // Name:    ConvertToUInt32
        // Title:   Convers bytes array (up to 4 bytes) into an unsigned 32 bits integer
        // Details: The input 4 butes are arranged in reverse order byte[0] is the ls byte[3] is
        //          the ms.
        // Version: V1.0 - Eyal shomrony
        //===========================================================================================
        private UInt32 ConvertToUInt32(byte[] b4Array)
        {
            UInt32 num = 0;
            UInt32 multiply = 1;

            for (int i = 0; i < b4Array.Length; i++)
            {
                if (i == 4) break;
                UInt32 iCurr = b4Array[i];
                num += multiply * iCurr;
                multiply = multiply << 8;
            }
            return num;
        }
    }
}
