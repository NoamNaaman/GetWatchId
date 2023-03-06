using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cardiac_Fast_Uploader
{
    class Protocol
    {

        public static Thread ProtocolThread;

        public struct COM_PORT
        {
            public static string CommPortNumber;
            public static SerialPort ComPort;
            public static string Firmware_version_string;
            public static bool IsOpen;
            public static bool Running;
        }
        

        //Protocol message
        struct Message
        {
            public UInt16 preamble;
            public UInt16 len;
            public Opcode opcode;
            public DevType source;
            public DevType dest;
            public UInt16 crc;
            public byte[] data;
        }

        //Protocol destination enums
        public enum DevType
        {
            DevTypePC = 0,
            DevTypeRMA,
            DevTypeWatchFE,
            DevTypeBootloader,
            DevTypeLast
        }

        //Protocol operation codes
        public enum Opcode
        {
            //Service
            //Service
            OpcodeZero = 0,               // 0
            OpcodeGetInfo = 1,
            OpcodeGetSelfTest = 2,
            //Firmware                   
            OpcodeStartFirmware = 3,
            OpcodeProcessFirmware = 4,
            OpcodeFinishFirmware = 5,
            OpcodeEraseFirmware = 6,
            OpcodePrepareFirmware = 7,
            //NOR Flash                   
            OpcodeGetNorFlashDataSize = 8,
            OpcodeGetNorFlashPage = 9,
            OpcodeNorFlashAckPage = 10,
            OpcodeNorFlashErase = 11,
            //Params                     
            OpcodeSetParam = 12,
            OpcodeGetParam = 13,
            //Protocol                    
            OpcodeMessageTimeout = 14,
            OpcodeAck = 15,
            OpcodeNack = 16,
            OpcodeSimulationData = 17,
            OpcodeGetSimData = 18,
            OpcodeStartUploadToServer = 19,
            OpcodeProcessUploadToServer = 20,
            OpcodeFinishUploadToServer = 21,
            OpcodeSetBleMode = 22,
            OpcodeGetCradleApList = 23,
            OpcodeCradleInfo = 24,
            // Tester                    
            OpcodeCollectTesterSamples = 25,
            OpcodeSendTesterSamples = 26,
            //Cradle / simulator                     
            OpcodeSetWatchScreen = 27,
            OpcodeSetSsidName = 28,
            OpcodeSetSsidPass = 29,
            OpcodeStartTcpUpload = 30,
            OpcodeSetDisplayString = 36,
            OpcodeSetSimulationName = 37,
            OpcodeSetWatchdogTest = 38,
            OpcodeSetWatchUniqueID = 39,
            // more NOR flash            
            OpcodeComputeDataCRC32 = 31,
            OpcodeReturningDataCRC32 = 32,
            OpcodeFlashSpare1 = 33,
            OpcodeFlashSpare2 = 34,
            OpcodeFlashSpare3 = 35,

            //Firmware                   
            Opcode_FE_StartFirmware = 40,
            Opcode_FE_ProcessFirmware = 41,
            Opcode_FE_FinishFirmware = 42,
            Opcode_FE_EraseFirmware = 43,
            Opcode_FE_PrepareFirmware = 44,
            OpcodeReturnSystemStatus = 53,
            OpcodeWatchMessage = 54,

            // File System Direct PC 
            OpcodeGetFileList = 62,
            OpcodeOpenFile = 63,
            OpcodeGetFileData = 64,
            OpcodeCloseFile = 65,
            OpcodeDeleteFile = 66,
            OpcodeSetDateTime = 69,
            OpcodePC_GetInfo = 70,

            // Flash files
            OpcodeGetFlash16kbBlock = 68,

            OpcodeDebug = 200,
            OpcodeLastOpcode = 255
        }


        //Posible error codes
        public enum ErrorCode
        {
            etErrorCodeSuccess = 0,
            etErrorCodeUsartNoData,
            etErrorCodeHwError,
            etErrorCodeDefineAnyError,
            etErrorCodeCrcError,
            etErrorCodeInvDst,
            etErrorCodeWrongSize
        }

        //Protocol states
        public enum MsgState
        {
            etMsgStateWaitingForPreamble = 0,
            etMsgStateReadingLength,
            etMsgStateReadingSource,
            etMsgStateReadingDest,
            etMsgStateReadingOpcode,
            etMsgStateReadingCrc,
            eMsgStatetReadingData,
            etMsgStateMessageReady,
            etMsgTimeout
        }
        


        public const UInt16 MESSAGE_PREAMBLE     = 0xF8AB;
        public const UInt16 PROTOCOL_HEADER_SIZE = 9;
        public const UInt16 PROTOCOL_CRC_SIZE    = 2;
        public const UInt16 MAX_DATA_SIZE        = 280;
        public const UInt32 PROTOCOL_TIMEOUT     = 5000;      //milli Seconds

        public const int PREAMBLE_OFFSET = 0;
        public const int LENGTH_OFFSET   = 2;
        public const int OPCODE_OFFSET   = 4;
        public const int SRC_OFFSET      = 5;
        public const int DST_OFFSET      = 6;
        public const int CRC_OFFSET      = 7;
        public const int DATA_OFFSET     = 9;

        public const int WATCHID_OFFSET = 0; // 2 after 
        public const int WATCHID_LENGTH = 8; // 8 bytes

        static int subState = 0;
        
        static int dataIndex = 0;
        static DateTime StartTime = new DateTime();
        static TimeSpan timeout = new TimeSpan(PROTOCOL_TIMEOUT * TimeSpan.TicksPerMillisecond);
        static TimeSpan rxTime = new TimeSpan(0);
        static MsgState state = MsgState.etMsgStateWaitingForPreamble;
        static MsgState msgState = MsgState.etMsgStateWaitingForPreamble;
        private static Message rxMessage;
        static byte[] rxData = new byte[1024];
        static int rxDataIdx = 0;
        static int rxDataLength = 0;
        private static byte[] LastSendCommand = null;
        public static string ErrMssg;

        //===========================================================================================
        // Name:    SetPort
        // Title:   Set the primary serial port to the class port to start the old basic communication
        //          engine (of Lev Zoosmanovskiy) work with updated approach
        // Version: V1 - Eyal shomrony
        //===========================================================================================
        public static void SetPort(SerialPort prt)
        {
            COM_PORT.ComPort = prt;
        }


        /**************************************************
        * Function name	: ProtocolGotMessage_CB
        * Returns	    :	
        * Arg		    : 
        * Created by	: Lev Zoosmanovskiy
        * Date created	: 20.11.20
        * Description	: Protocol Valid message handler
        * Notes		: 
        **************************************************/
        private static void ProtocolGotMessage_CB()
        {
            switch (rxMessage.opcode)
            {
                case Opcode.OpcodeZero:
                    break;
                case Opcode.OpcodeGetInfo:
                    break;
                case Opcode.OpcodeGetSelfTest:
                    break;
                case Opcode.OpcodeStartFirmware:
                    break;
                case Opcode.OpcodeProcessFirmware:
                    break;
                case Opcode.OpcodeFinishFirmware:
                    break;
                case Opcode.OpcodeEraseFirmware:
                    break;
                case Opcode.OpcodePrepareFirmware:
                    break;
                case Opcode.OpcodeSetParam:
                    break;
                case Opcode.OpcodeGetParam:
                    break;
                case Opcode.OpcodeMessageTimeout:
                    break;
                case Opcode.OpcodeAck:
                    UInt32 ackPage = BitConverter.ToUInt32(rxMessage.data, 0);
                    //FW_Uploader.setAck(ackPage);
                    break;
                case Opcode.OpcodeNack:
                    break;
                case Opcode.OpcodeLastOpcode:
                    break;
                default:
                    break;
            }
        }

        /**************************************************
        * Function name	: ComListener
        * Returns	    :	
        * Arg		    : string port, int baudRate
        * Created by	: Lev Zoosmanovskiy
        * Date created	: 20.11.20
        * Description	: Comm port listener thread
        * Notes		: 
        **************************************************/
        static byte[] rxBuff = new byte[16];
        private static void ComListener()
        {
            int byteToRead = 0;
            
            while (COM_PORT.Running)
            {

                if (COM_PORT.ComPort != null)
                {
                    if (COM_PORT.ComPort.IsOpen)
                    {
                        byteToRead = COM_PORT.ComPort.BytesToRead;

                        while (byteToRead > 0)
                        {
                            try
                            {
                                COM_PORT.ComPort.Read(rxBuff, 0, 1);
                                byteToRead--;

                                msgState = ProtocolParseMsg(rxBuff[0]);

                                if (msgState == MsgState.etMsgStateMessageReady)
                                {
                                    //ProtocolCalcCrc(byte[] buff, UInt16 len)
                                    //Clear the buffer CRC
                                    rxData[7] = 0;
                                    rxData[8] = 0;
                                    if (ProtocolCalcCrc(rxData, (UInt16)(rxDataIdx)) == rxMessage.crc)
                                    {
                                        //Parse valid message
                                        //mesageParser(&sRxMessage);
                                        //Thread.Sleep(100);
                                        ProtocolGotMessage_CB();
                                    //    Loader_UI.PrintLog("GOT MSG");
                                    }
                                    msgState = MsgState.etMsgStateWaitingForPreamble;
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {

                                //Loader_UI.PrintLog("Can't read from serial");
                                //Loader_UI.PrintLog(ex.Message.ToString());
                            }
                            //Thread.Sleep(1);
                        }
                    }
                }
                Thread.Sleep(1);
            }
        }

        /**************************************************
        * Function name	: ProtocolParseMsg
        * Returns	:	
        * Arg		: 
        * Created by	: Lev Zoosmanovskiy
        * Date created	: 20/03/2020
        * Description	: Protocol message collecting state machine
        * Notes		: //Preamle -> length -> opcode -> crc -> data...
        **************************************************/
        public static MsgState ProtocolParseMsg(byte rxByte)
        {
            switch (state)
            {
                //Waiting for message preamble (2 bytes)
                case MsgState.etMsgStateWaitingForPreamble:
                    {
                        UInt16 preamble = MESSAGE_PREAMBLE;
                        byte preamble_a = (byte)(preamble & 0xFF);
                        byte preamble_b = (byte)(preamble >> 8 & 0x00FF);

                        //First byte of preamle
                        if (subState == 0)
                        {
                            if (rxByte == preamble_a)
                            {
                                //Get the time of the first received byte
                                StartTime = DateTime.Now;
                                rxDataIdx = 0;

                                rxMessage.preamble = rxByte;
                                subState++;
                            }
                        }
                        //Second byte of preamle
                        else if (subState == 1)
                        {
                            if (rxByte == preamble_b)
                            {
                                rxMessage.preamble |= (UInt16)(rxByte << 8 & 0xFF00);
                                state = MsgState.etMsgStateReadingLength;
                                dataIndex = 0;
                                subState = 0;
                            }
                            else
                            {
                                subState = 0;
                            }
                        }
                    }
                    break;
                //Reading length (2 bytes)
                case MsgState.etMsgStateReadingLength:
                    {
                        //First byte
                        if (subState == 0)
                        {
                            subState++;
                            rxMessage.len = rxByte;
                        }
                        //Second byte
                        else if (subState == 1)
                        {
                            subState = 0;
                            rxMessage.len |= (UInt16)(rxByte << 8 & 0xFF00);
                            //Valid length?
                            if (rxMessage.len <= MAX_DATA_SIZE)
                            {
                                state = MsgState.etMsgStateReadingOpcode; //Next state
                            }
                            else
                            {
                                state = MsgState.etMsgStateWaitingForPreamble; //Invalid data size
                            }
                        }
                    }
                    break;
                //Reading opcode
                case MsgState.etMsgStateReadingOpcode:
                    rxMessage.opcode = (Opcode)rxByte;
                    state = MsgState.etMsgStateReadingSource; //Next state
                    break;

                //Reading source
                case MsgState.etMsgStateReadingSource:
                    rxMessage.source = (DevType)rxByte;
                    state = MsgState.etMsgStateReadingDest; //Next state        
                    break;

                case MsgState.etMsgStateReadingDest:
                    //Reading destination
                    state = MsgState.etMsgStateReadingCrc; //Next state
                    rxMessage.dest = (DevType)rxByte;
                    break;

                //Reading CRC (2 bytes)
                case MsgState.etMsgStateReadingCrc:
                    //First byte
                    if (subState == 0)
                    {
                        rxMessage.crc = rxByte;
                        subState++;
                    }
                    //Second byte
                    else if (subState == 1)
                    {
                        rxMessage.crc |= (UInt16)(rxByte << 8);
                        if (rxMessage.len == 0)
                        {
                            state = MsgState.etMsgStateMessageReady;//Next state
                        }
                        else
                        {
                            state = MsgState.eMsgStatetReadingData; //Next state
                        }
                        subState = 0;
                    }
                    break;
                //Collecting data
                case MsgState.eMsgStatetReadingData:
                    //Overflow protection
                    if (dataIndex <= MAX_DATA_SIZE)
                    {
                        //Collect the data
                        rxMessage.data[dataIndex] = rxByte;
                        dataIndex++;
                    }
                    //End of the data 
                    if (dataIndex >= rxMessage.len)
                    {
                        dataIndex = 0;
                        state = MsgState.etMsgStateMessageReady;
                    }
                    break;
            }

            //Timeout 
            if (StartTime + timeout < DateTime.Now)
            {
                //ProtocolSendMessage( OpcodeMessageTimeout, NULL,0);  //send Nack message  
                state = MsgState.etMsgStateWaitingForPreamble;
            }
            //Get copy of data for CRC
            rxData[rxDataIdx++] = rxByte;

            //Message ready
            if (state == MsgState.etMsgStateMessageReady)
            {
                rxTime = DateTime.Now - StartTime;
                rxDataLength = rxMessage.len + PROTOCOL_HEADER_SIZE;
                state = MsgState.etMsgStateWaitingForPreamble;
                return MsgState.etMsgStateMessageReady;
            }
            
            //Return current state
            return state;
        }


        /**************************************************
        * Function name	: OpenComPort
        * Returns	    :	
        * Arg		    : string port, int baudRate
        * Created by	: Lev Zoosmanovskiy
        * Date created	: 20.11.20
        * Description	: Opens Comm port and starts the listener thread
        * Notes		: 
        **************************************************/
        public static bool OpenComPort(string port, int baudRate)
        {
            bool ret = false;
            try
            {
                if (port == "" || port == "")
                {
                    //Loader_UI.PrintLog("Configure port");
                }
                else
                {
                    COM_PORT.ComPort = new SerialPort();

                    //Loader_UI.PrintLog("Opening port...");
                    COM_PORT.ComPort.PortName = port;
                    COM_PORT.ComPort.BaudRate = baudRate;
                    COM_PORT.ComPort.Open();
                    COM_PORT.CommPortNumber = port;
                    COM_PORT.IsOpen = COM_PORT.ComPort.IsOpen;
                    
                    COM_PORT.Running = true;
                    //Loader_UI.PrintLog("Success");
                    ret = true;
                    rxMessage.data = new byte[256];
                    ProtocolThread = new Thread(ComListener);
                    ProtocolThread.Start();
                }
            }
            catch (Exception ex)
            {
                //Loader_UI.PrintLog(ex.Message.ToString());
            }

            return ret;
        }

        /**************************************************
       * Function name	: CloseComPort
       * Returns	    :	
       * Arg		    : 
       * Created by	: Lev Zoosmanovskiy
       * Date created	: 20.11.20
       * Description	: Closes Comm port and stops the listener thread
       * Notes		: 
       **************************************************/
        public static void CloseComPort()
        {
            if (COM_PORT.ComPort != null)
            {
                if (COM_PORT.ComPort.IsOpen)
                {
                    COM_PORT.ComPort.Close();
                    COM_PORT.Running = false;
                }
            }
        }
        
        /**************************************************
        * Function name	: ProtocolSendMessage
        * Returns	:	
        * Arg		: 
        * Created by	: Lev Zoosmanovskiy
        * Date created	: 20/01/2020
        * Description	: Builds and sends protocol message
        * Notes		: 
        **************************************************/       
        public static int SendMessage(DevType dst, Opcode opcode, byte[] data, UInt16 len)
        {
            UInt16 dataLen = len;
            UInt16 msgSize = (UInt16)(dataLen + PROTOCOL_HEADER_SIZE);
            byte[] txBuff = new byte[MAX_DATA_SIZE];
            //Fill the header

            //Preamble
            byte[] byteArray = BitConverter.GetBytes(MESSAGE_PREAMBLE);
            
            Array.Copy(byteArray, 0, txBuff, PREAMBLE_OFFSET, 2);
            //Message size
            byteArray = BitConverter.GetBytes(dataLen);
            Array.Copy(byteArray, 0, txBuff, LENGTH_OFFSET, 2);
            //Operation code
            txBuff[OPCODE_OFFSET] = (byte)opcode;
            //Source
            txBuff[SRC_OFFSET] = (byte)dst;
            //Destination
            txBuff[DST_OFFSET] = (byte)DevType.DevTypePC;
            
            //Copy data
            if (msgSize > 0 && dataLen <= MAX_DATA_SIZE)
            {
                Array.Copy(data, 0, txBuff, DATA_OFFSET, len);
            }
            else
            {
                return (int)ErrorCode.etErrorCodeWrongSize;
            }

            //Calc the Checksum
            UInt16 crc = ProtocolCalcCrc(txBuff, msgSize);
            byteArray = BitConverter.GetBytes(crc);
            Array.Copy(byteArray, 0, txBuff, CRC_OFFSET, PROTOCOL_CRC_SIZE);

            LastSendCommand = new byte[msgSize];

            Array.Copy(txBuff, 0, LastSendCommand, 0, msgSize);

            ErrMssg = "";
            if (COM_PORT.ComPort != null)
            {
                if (COM_PORT.ComPort.IsOpen)
                {
                    try
                    {
                        COM_PORT.ComPort.Write(txBuff, 0, msgSize);
                    }
                    catch (Exception ex)
                    {
                        ErrMssg = "Port send failed " + ex.Data.ToString();
                        //MessageBox.Show(msg);
                        return 1;
                    }

                }
            }
              
            return 0;
        }

        //===========================================================================================
        // Name:    SendMessageAgain
        // Title:   Send again the last sended message
        // Version: V1 - Eyal shomrony
        //===========================================================================================
        public static void SendMessageAgain( )
        {
            if (COM_PORT.ComPort != null)
            {
                if (COM_PORT.ComPort.IsOpen)
                {
                    ErrMssg = "";
                    try
                    {
                        COM_PORT.ComPort.Write(LastSendCommand, 0, LastSendCommand.Length);
                    }
                    catch (Exception ex)
                    {
                        ErrMssg = "Port name failed " + ex.Data.ToString();
                        return;
                    }
                }
            }
        }

        /**************************************************
        * Function name	: ProtocolCalcCrc
        * Returns	:	
        * Arg		: 
        * Created by	: Lev Zoosmanovskiy
        * Date created	: 20/01/2020
        * Description	: Calculates check sum for the protocol messages
        * Notes		: //-- CRC-16-CCITT (poly 0x1021) --//
        **************************************************/
        public static UInt16 ProtocolCalcCrc(byte[] buff, UInt16 len)
        {
            byte tmp;
            UInt16 crc = 0xFFFF;
            int i = 0;

            while (len-- > 0)
            {
                tmp = (byte)(crc >> 8 ^ buff[i++]);
                tmp ^= (byte)(tmp >> 4);
                crc = (UInt16)((crc << 8) ^ ((UInt16)(tmp << 12)) ^ ((UInt16)(tmp << 5)) ^ ((UInt16)tmp));
            }

            return crc;
        }


    }
}
