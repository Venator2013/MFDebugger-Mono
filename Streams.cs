////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Portions Copyright (c) Secret Labs LLC.  All rights reserved.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Management;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Net;
using System.Net.Sockets;
using LibUsbDotNet.DeviceNotify;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using LibUsbDotNet.Info;

namespace Microsoft.SPOT.Debugger
{
#if REMOVED_OTHER_STREAMS
    // This is an internal object implementing IAsyncResult with fields
    // for all of the relevant data necessary to complete the IO operation.
    // This is used by AsyncFSCallback and all of the async methods.
    unsafe internal class AsyncFileStream_AsyncResult : IAsyncResult
    {
        private unsafe static readonly IOCompletionCallback s_callback = new IOCompletionCallback( DoneCallback );

        internal AsyncCallback     m_userCallback;
        internal Object            m_userStateObject;
        internal ManualResetEvent  m_waitHandle;

        internal GCHandle          m_bufferHandle;    // GCHandle to pin byte[].
        internal bool              m_bufferIsPinned;  // Whether our m_bufferHandle is valid.

        internal bool              m_isWrite;         // Whether this is a read or a write
        internal bool              m_isComplete;
        internal bool              m_EndXxxCalled;    // Whether we've called EndXxx already.
        internal int               m_numBytes;        // number of bytes read OR written
        internal int               m_errorCode;
        internal NativeOverlapped* m_overlapped;
        
        internal AsyncFileStream_AsyncResult( AsyncCallback userCallback, Object stateObject, bool isWrite )
        {
            m_userCallback    = userCallback;
            m_userStateObject = stateObject;
            m_waitHandle      = new ManualResetEvent( false );
	jh
            m_isWrite         = isWrite;

            Overlapped overlapped = new Overlapped( 0, 0, IntPtr.Zero, this );

            m_overlapped = overlapped.Pack( s_callback, null );            
        }

        public virtual Object AsyncState
        {
            get { return m_userStateObject; }
        }

        public bool IsCompleted
        {
            get { return m_isComplete;  }
            set { m_isComplete = value; }
        }

        public WaitHandle AsyncWaitHandle
        {
            get { return m_waitHandle; }
        }

        public bool CompletedSynchronously
        {
            get { return false; }
        }

        internal void SignalCompleted()
        {
            AsyncCallback userCallback = null;

            lock(this)
            {
                if(m_isComplete == false)
                {
                    userCallback = m_userCallback;

                    ManualResetEvent wh = m_waitHandle;
                    if(wh != null && wh.Set() == false)
                    {
                        Native.ThrowIOException( string.Empty );
                    }

                    // Set IsCompleted to true AFTER we've signalled the WaitHandle!
                    // Necessary since we close the WaitHandle after checking IsCompleted,
                    // so we could cause the SetEvent call to fail.
                    m_isComplete = true;

                    ReleaseMemory();
                }
            }

            if(userCallback != null)
            {
                userCallback( this );
            }
        }

        internal void WaitCompleted()
        {
            ManualResetEvent wh = m_waitHandle;
            if(wh != null)
            {
                if(m_isComplete == false)
                {
                    wh.WaitOne();
                    // There's a subtle race condition here.  In AsyncFSCallback,
                    // I must signal the WaitHandle then set _isComplete to be true,
                    // to avoid closing the WaitHandle before AsyncFSCallback has
                    // signalled it.  But with that behavior and the optimization
                    // to call WaitOne only when IsCompleted is false, it's possible
                    // to return from this method before IsCompleted is set to true.
                    // This is currently completely harmless, so the most efficient
                    // solution of just setting the field seems like the right thing
                    // to do.     -- BrianGru, 6/19/2000
                    m_isComplete = true;
                }
                wh.Close();
	}klj
        }

        internal NativeOverlapped* OverlappedPtr
        {
            get { return m_overlapped; }
        }

        internal unsafe void ReleaseMemory()
        {
            if(m_overlapped != null)
            {
                Overlapped.Free( m_overlapped );
                m_overlapped = null;
            }

            UnpinBuffer();
        }

        internal void PinBuffer( byte[] buffer )
        {
            m_bufferHandle   = GCHandle.Alloc( buffer, GCHandleType.Pinned );
            m_bufferIsPinned = true;
        }

        internal void UnpinBuffer()
        {
            if(m_bufferIsPinned)
            {
                m_bufferHandle.Free();
                m_bufferIsPinned = false;
            }
        }

        // this callback is called by a free thread in the threadpool when the IO operation completes.
        unsafe private static void DoneCallback( uint errorCode, uint numBytes, NativeOverlapped* pOverlapped )
        {
            if(errorCode == Native.ERROR_OPERATION_ABORTED)
            {
                numBytes  = 0;
                errorCode = 0;
            }

            // Unpack overlapped
            Overlapped overlapped = Overlapped.Unpack( pOverlapped );
            // Free the overlapped struct in EndRead/EndWrite.

            // Extract async result from overlapped
            AsyncFileStream_AsyncResult asyncResult = (AsyncFileStream_AsyncResult)overlapped.AsyncResult;


            asyncResult.m_numBytes  = (int)numBytes;
            asyncResult.m_errorCode = (int)errorCode;

            asyncResult.SignalCompleted();
        }
    }

    public class GenericAsyncStream : System.IO.Stream, IDisposable, WireProtocol.IStreamAvailableCharacters
    {
        protected SafeHandle m_handle;
        protected ArrayList m_outstandingRequests;

        protected GenericAsyncStream(SafeHandle handle)
        {
            System.Diagnostics.Debug.Assert(handle != null);

            m_handle = handle;

            if(ThreadPool.BindHandle( m_handle ) == false)
            {
                throw new IOException( "BindHandle Failed" );
            }

            m_outstandingRequests = ArrayList.Synchronized(new ArrayList());
        }

        ~GenericAsyncStream()
        {
            Dispose( false );
        }

        public void CancelPendingIO()
        {
            lock(m_outstandingRequests.SyncRoot)
            {
                for(int i = m_outstandingRequests.Count - 1; i >= 0; i--)
                {
                    AsyncFileStream_AsyncResult asfar = (AsyncFileStream_AsyncResult)m_outstandingRequests[i];
                    asfar.SignalCompleted();
                }

                m_outstandingRequests.Clear();
            }
        }
        
        protected override void Dispose( bool disposing )
        {            
            // Nothing will be done differently based on whether we are disposing vs. finalizing.
            lock (this)
            {
                if (m_handle != null && !m_handle.IsInvalid)
                {
                    if(disposing)
                    {
                        CancelPendingIO();
                    }

                    m_handle.Close();
                    m_handle.SetHandleAsInvalid();
                }                
            }

            base.Dispose(disposing);
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override long Length
        {
            get { throw NotImplemented(); }
        }

        public override long Position
        {
            get { throw NotImplemented(); }
            set { throw NotImplemented(); }
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            return BeginReadCore(buffer, offset, count, callback, state);
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            return BeginWriteCore(buffer, offset, count, callback, state);
        }

        public override void Close()
        {
            Dispose(true);
        }

        public override int EndRead( IAsyncResult asyncResult )
        {
            AsyncFileStream_AsyncResult afsar = CheckParameterForEnd( asyncResult, false );

            afsar.WaitCompleted();

            m_outstandingRequests.Remove( afsar );

            // Now check for any error during the read.
            if(afsar.m_errorCode != 0) throw new IOException( "Async Read failed", afsar.m_errorCode );

            return afsar.m_numBytes;
        }

        public override void EndWrite( IAsyncResult asyncResult )
        {
            AsyncFileStream_AsyncResult afsar = CheckParameterForEnd( asyncResult, true );

            afsar.WaitCompleted();

            m_outstandingRequests.Remove( afsar );

            // Now check for any error during the write.
            if(afsar.m_errorCode != 0) throw new IOException( "Async Write failed", afsar.m_errorCode );
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            IAsyncResult result = BeginRead(buffer, offset, count, null, null);
            return EndRead( result );
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw NotImplemented();
        }

        public override void SetLength(long value)
        {
            throw NotImplemented();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            IAsyncResult result = BeginWrite(buffer, offset, count, null, null);
            EndWrite( result );
        }

        public SafeHandle Handle
        {
            get
            {
                return m_handle;
            }
        }

        public virtual int AvailableCharacters
        {
            get
            {
                return 0;
            }
        }

        private Exception NotImplemented()
        {
            return new NotSupportedException( "Not Supported" );
        }

        private void CheckParametersForBegin( byte[] array, int offset, int count )
        {
            if(array == null) throw new ArgumentNullException( "array" );

            if(offset < 0) throw new ArgumentOutOfRangeException( "offset" );

            if(count < 0 || array.Length - offset < count) throw new ArgumentOutOfRangeException( "count" );

            if(m_handle.IsInvalid)
            {
                throw new ObjectDisposedException( null );
            }
        }

        private AsyncFileStream_AsyncResult CheckParameterForEnd( IAsyncResult asyncResult, bool isWrite )
        {
            if(asyncResult == null) throw new ArgumentNullException( "asyncResult" );

            AsyncFileStream_AsyncResult afsar = asyncResult as AsyncFileStream_AsyncResult;
            if(afsar == null || afsar.m_isWrite != isWrite) throw new ArgumentException( "asyncResult" );
            if(afsar.m_EndXxxCalled) throw new InvalidOperationException( "EndRead called twice" );
            afsar.m_EndXxxCalled = true;

            return afsar;
        }

        private unsafe IAsyncResult BeginReadCore( byte[] array, int offset, int count, AsyncCallback userCallback, Object stateObject )
        {
            CheckParametersForBegin( array, offset, count );

            AsyncFileStream_AsyncResult asyncResult = new AsyncFileStream_AsyncResult( userCallback, stateObject, false );

            if(count == 0)
            {
                asyncResult.SignalCompleted();
            }
            else
            {
                // Keep the array in one location in memory until the OS writes the
                // relevant data into the array.  Free GCHandle later.
                asyncResult.PinBuffer( array );

                fixed(byte* p = array)
                {
                    int  numBytesRead = 0;
                    bool res;

                    res = Native.ReadFile( m_handle.DangerousGetHandle(), p + offset, count, out numBytesRead, asyncResult.OverlappedPtr );
                    if(res == false)
                    {
                        if(HandleErrorSituation( "BeginRead", false ))
                        {
                            asyncResult.SignalCompleted();
                        }
                        else
                        {
                            m_outstandingRequests.Add( asyncResult );
                        }
                    }                    
                }
            }

            return asyncResult;
        }

        private unsafe IAsyncResult BeginWriteCore( byte[] array, int offset, int count, AsyncCallback userCallback, Object stateObject )
        {
            CheckParametersForBegin( array, offset, count );

            AsyncFileStream_AsyncResult asyncResult = new AsyncFileStream_AsyncResult( userCallback, stateObject, true );

            if(count == 0)
            {
                asyncResult.SignalCompleted();
            }
            else
            {
                // Keep the array in one location in memory until the OS writes the
                // relevant data into the array.  Free GCHandle later.
                asyncResult.PinBuffer( array );

                fixed(byte* p = array)
                {
                    int  numBytesWritten = 0;
                    bool res;

                    res = Native.WriteFile( m_handle.DangerousGetHandle(), p + offset, count, out numBytesWritten, asyncResult.OverlappedPtr );
                    if(res == false)
                    {
                        if(HandleErrorSituation( "BeginWrite", true ))
                        {
                            asyncResult.SignalCompleted();
                        }
                        else
                        {
                            m_outstandingRequests.Add( asyncResult );
                        }
                    }
                }
            }

            return asyncResult;
        }

        protected virtual bool HandleErrorSituation( string msg, bool isWrite )
        {
            int hr = Marshal.GetLastWin32Error();

            // For invalid handles, detect the error and close ourselves
            // to prevent a malicious app from stealing someone else's file
            // handle when the OS recycles the handle number.
            if(hr == Native.ERROR_INVALID_HANDLE)
            {
                m_handle.Close();
            }

            if(hr != Native.ERROR_IO_PENDING)
            {
                if(isWrite == false && hr == Native.ERROR_HANDLE_EOF)
                {
                    throw new EndOfStreamException( msg );
                }

                throw new IOException( msg, hr );
            }

            return false;
        }


        #region IDisposable Members

        void IDisposable.Dispose()
        {
            base.Dispose( true );

            Dispose( true );

            GC.SuppressFinalize( this );
        }

        #endregion
}

    public class AsyncFileStream : GenericAsyncStream
    {
        private string m_fileName = null;

        public AsyncFileStream( string file, System.IO.FileShare share ) : base( OpenHandle( file, share ) )
        {
            m_fileName = file;
        }

        static private SafeFileHandle OpenHandle( string file, System.IO.FileShare share )
        {
            if(file == null || file.Length == 0)
            {
                throw new ArgumentNullException( "file" );
            }

            SafeFileHandle handle = Native.CreateFile(file, Native.GENERIC_READ | Native.GENERIC_WRITE, share, Native.NULL, System.IO.FileMode.Open, Native.FILE_FLAG_OVERLAPPED, Native.NULL);
            
            if(handle.IsInvalid)
            {
                throw new InvalidOperationException( String.Format( "Cannot open {0}", file ) );
            }

            return handle;
        }

        public String Name
        {
            get
            {
                return m_fileName;
            }
        }

        public unsafe override int AvailableCharacters
        {
            get
            {
                int bytesRead;
                int totalBytesAvail;
                int bytesLeftThisMessage;

                if(Native.PeekNamedPipe( m_handle.DangerousGetHandle(), (byte*)Native.NULL, 0, out bytesRead, out totalBytesAvail, out bytesLeftThisMessage ) == false)
                {
                    totalBytesAvail = 1;
                }

                return totalBytesAvail;
            }
        }
    }
#endif

    // NOTE: this is a serial stream compatible with Mono
    public class AsyncSerialStream : System.IO.Stream, IDisposable, WireProtocol.IStreamAvailableCharacters
    {
		private SerialPort _serialPort = null;
		private Thread _serialPortThread = null;
		
		bool _disposing = false;
		bool _disposed = false;

		private object _writeLock = new object();

        public AsyncSerialStream( string port, uint baudrate )
        {
			_serialPort = new SerialPort(port, (int)baudrate, Parity.None, 8, StopBits.One);
			_serialPort.Handshake = Handshake.None;
			_serialPort.ReadTimeout = System.Threading.Timeout.Infinite;
			_serialPort.Open();
        }

                ~AsyncSerialStream()
        {
            Dispose( false );
        }

        protected override void Dispose(bool disposing)
        {
			_disposing = true;
			
            //consistent cleanup if we are disposed or finalized.
            lock (this)
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    if (disposing)
                    {
                        //CancelPendingIO();
                    }
                    _serialPort.Dispose();
                }
            }

			_disposed = true;
			
            base.Dispose(disposing);
        }

        public override void Close()
        {
            Dispose(true);
        }

        #region IDisposable Members

        void IDisposable.Dispose()
        {
            base.Dispose(true);

            Dispose(true);

            GC.SuppressFinalize(this);
        }
        #endregion

        public void ConfigureXonXoff( bool fEnable )
        {
            //TODO: implement this function
			throw new NotImplementedException(); 
        }

        static public PortDefinition[] EnumeratePorts()
        {
            SortedList lst = new SortedList();

			string[] portNames = System.IO.Ports.SerialPort.GetPortNames();
			for (int iPortName = 0; iPortName < portNames.Length; iPortName++)
			{
				lst.Add(portNames[iPortName], PortDefinition.CreateInstanceForSerial(portNames[iPortName], portNames[iPortName], 115200));
			}
			
            /* NOTE: on Mac OS X, the SerialPort.GetPortNames() function does not return any values.
			 *       For now (on Mac OS X), it is necessary to manually add serial port entries. 
			 *       Here are some sample serial port definition entries. */
            //lst.Add("/dev/tty.KeySerial1", PortDefinition.CreateInstanceForSerial("/dev/tty.KeySerial1", "/dev/tty.KeySerial1", 115200));
			//lst.Add("/dev/ttyUSB0", PortDefinition.CreateInstanceForSerial("/dev/ttyUSB0", "/dev/ttyUSB0", 115200));
			//lst.Add("COM2", PortDefinition.CreateInstanceForSerial("COM2", "COM2", 115200));

            ICollection      col = lst.Values;
            PortDefinition[] res = new PortDefinition[col.Count];

            col.CopyTo( res, 0 );

            return res;
        }
		
		public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override long Length
        {
            get { throw NotImplemented(); }
        }

        public override long Position
        {
            get { throw NotImplemented(); }
            set { throw NotImplemented(); }
        }
		
		public override void Flush()
        {
			_serialPort.BaseStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
			IAsyncResult result = _serialPort.BaseStream.BeginRead(buffer, offset, count, null, null);
			result.AsyncWaitHandle.WaitOne();
			return _serialPort.BaseStream.EndRead(result);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw NotImplemented();
        }

        public override void SetLength(long value)
        {
            throw NotImplemented();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
			// THIS IS CRITICAL: ONLY ONE WRITER AT A TIME!
			lock (_writeLock)
			{
				for (int i = offset; i < offset + count; i++)
				{
				IAsyncResult result = _serialPort.BaseStream.BeginWrite(buffer, i, 1, null, null);
				while (!result.IsCompleted)
					result.AsyncWaitHandle.WaitOne();
					
				_serialPort.BaseStream.EndWrite(result);
				}
            }
        }

		public virtual int AvailableCharacters
        {
            get
            {	
				return _serialPort.BytesToRead;
			}
        }

		private Exception NotImplemented()
		{
            return new NotSupportedException( "Not Supported" );
        }
    }

#if REMOVED_OLD_SERIAL_STREAM
    public class AsyncSerialStream : AsyncFileStream
    {
        public AsyncSerialStream( string port, uint baudrate ) : base( port, System.IO.FileShare.None )
        {
            Native.COMMTIMEOUTS cto = new Native.COMMTIMEOUTS(); cto.Initialize();
            Native.DCB          dcb = new Native.DCB         (); dcb.Initialize();

            Native.GetCommState( m_handle.DangerousGetHandle(), ref dcb );

            dcb.BaudRate = baudrate;
            dcb.ByteSize = 8;
            dcb.StopBits = 0;

            dcb.__BitField = 0;
            dcb.__BitField &= ~Native.DCB.mask_fDtrControl  ;
            dcb.__BitField &= ~Native.DCB.mask_fRtsControl  ;
            dcb.__BitField |=  Native.DCB.mask_fBinary      ;
            dcb.__BitField &= ~Native.DCB.mask_fParity      ;
            dcb.__BitField &= ~Native.DCB.mask_fOutX        ;
            dcb.__BitField &= ~Native.DCB.mask_fInX         ;
            dcb.__BitField &= ~Native.DCB.mask_fErrorChar   ;
            dcb.__BitField &= ~Native.DCB.mask_fNull        ;
            dcb.__BitField |=  Native.DCB.mask_fAbortOnError;

            Native.SetCommState( m_handle.DangerousGetHandle(), ref dcb );

            Native.SetCommTimeouts( m_handle.DangerousGetHandle(), ref cto );
        }

        public override int AvailableCharacters
        {
            get
            {
                Native.COMSTAT cs = new Native.COMSTAT(); cs.Initialize();
                uint           errors;

                Native.ClearCommError( m_handle.DangerousGetHandle(), out errors, ref cs );

                return (int)cs.cbInQue;
            }
        }

        protected override bool HandleErrorSituation( string msg, bool isWrite )
        {
            if(Marshal.GetLastWin32Error() == Native.ERROR_OPERATION_ABORTED)
            {
                Native.COMSTAT cs = new Native.COMSTAT(); cs.Initialize();
                uint           errors;

                Native.ClearCommError( m_handle.DangerousGetHandle(), out errors, ref cs );

                return true;
            }

            return base.HandleErrorSituation( msg, isWrite );
        }

        public void ConfigureXonXoff( bool fEnable )
        {
            Native.DCB dcb = new Native.DCB(); dcb.Initialize();

            Native.GetCommState( m_handle.DangerousGetHandle(), ref dcb );

            if(fEnable)
            {
                dcb.__BitField |= Native.DCB.mask_fOutX;
            }
            else
            {
                dcb.__BitField &= ~Native.DCB.mask_fOutX;
            }

            Native.SetCommState( m_handle.DangerousGetHandle(), ref dcb );
        }

        static public PortDefinition[] EnumeratePorts()
        {
            SortedList lst = new SortedList();

            try
            {
                RegistryKey key = Registry.LocalMachine.OpenSubKey( @"HARDWARE\DEVICEMAP\SERIALCOMM" );

                foreach(string name in key.GetValueNames())
                {
                    string         val = (string)key.GetValue( name );
                    PortDefinition pd  = PortDefinition.CreateInstanceForSerial( val, @"\\.\" + val, 115200 );

                    lst.Add( val, pd );
                }
            }
            catch
            {
            }

            ICollection      col = lst.Values;
            PortDefinition[] res = new PortDefinition[col.Count];

            col.CopyTo( res, 0 );

            return res;
        }
    }
#endif

    public class AsyncNetworkStream : NetworkStream, WireProtocol.IStreamAvailableCharacters
    {
        public AsyncNetworkStream(Socket socket, bool ownsSocket)
            : base(socket, ownsSocket)
        {
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        #region IStreamAvailableCharacters

        int WireProtocol.IStreamAvailableCharacters.AvailableCharacters
        {
            get
            {
                return this.Socket.Available;
            }
        }
        #endregion 
    }

    [Serializable]
    public class PortDefinition_Serial : PortDefinition
    {
        uint m_baudRate;

        public PortDefinition_Serial( string displayName, string port, uint baudRate ) : base(displayName, port)
        {
            m_baudRate = baudRate;
        }

        public uint BaudRate
        {
            get
            {
                return m_baudRate;
            }

            set
            {
                m_baudRate = value;
            }
        }

        public override Stream CreateStream()
        {
            return new AsyncSerialStream( m_port, m_baudRate );
        }

        public override string PersistName
        {
            get { return m_displayName; }
        }
    }

public class UsbDeviceDiscovery : IDisposable
{
	public enum DeviceChanged : ushort
	{
		None = 0,
		Configuration = 1,
		DeviceArrival = 2,
		DeviceRemoval = 3,
		Docking = 4,
	}

	public delegate void DeviceChangedEventHandler (DeviceChanged change);

	IDeviceNotifier m_UsbDeviceNotifier;
	DeviceChangedEventHandler m_subscribers;

	public UsbDeviceDiscovery ()
	{

	}

	~UsbDeviceDiscovery ()
	{
		try
		{
			Dispose ();
		} catch
		{
		}
	}

	[MethodImplAttribute (MethodImplOptions.Synchronized)]
	public void Dispose ()
	{


		if (m_UsbDeviceNotifier != null)
		{
			m_UsbDeviceNotifier = null;
			m_subscribers = null;
		}
		GC.SuppressFinalize (this);

	}
	// subscribing to this event allows applications to be notified when USB devices are plugged and unplugged
	// as well as configuration changed and docking; upon receiving teh notification the applicaion can decide
	// to call UsbDeviceDiscovery.EnumeratePorts to get an updated list of Usb devices
	public event DeviceChangedEventHandler OnDeviceChanged
	{
		[MethodImplAttribute(MethodImplOptions.Synchronized)]
		add
		{
			TryInstanceNotification (value);
		}

		[MethodImplAttribute(MethodImplOptions.Synchronized)]
		remove
		{

			m_subscribers -= value;

			if (m_subscribers == null)
			{
				if (m_UsbDeviceNotifier != null)
				{                        
					m_UsbDeviceNotifier = null;
				}
			}

		}
	}

	private void TryInstanceNotification (DeviceChangedEventHandler handler)
	{
		m_UsbDeviceNotifier = DeviceNotifier.OpenDeviceNotifier ();
		m_UsbDeviceNotifier.OnDeviceNotify += new EventHandler<DeviceNotifyEventArgs> (HandleDeviceInstance); 
		m_subscribers += handler;     
	}

	private void HandleDeviceInstance (object sender, DeviceNotifyEventArgs args)
	{	
		if (m_subscribers != null)
		{

			EventType deviceEvent = args.EventType;

			if (deviceEvent.Equals (EventType.DeviceArrival))
			{
				m_subscribers (DeviceChanged.DeviceArrival);                    
			} else if (deviceEvent.Equals (EventType.DeviceRemoveComplete))
			{
				m_subscribers (DeviceChanged.DeviceRemoval);
			}
		}
	}
}


public class AsyncUsbStream :System.IO.Stream, IDisposable, WireProtocol.IStreamAvailableCharacters
{
	// IOCTL codes
	private const int IOCTL_SPOTUSB_READ_AVAILABLE = 0;
	private const int IOCTL_SPOTUSB_DEVICE_HASH = 1;
	private const int IOCTL_SPOTUSB_MANUFACTURER = 2;
	private const int IOCTL_SPOTUSB_PRODUCT = 3;
	private const int IOCTL_SPOTUSB_SERIAL_NUMBER = 4;
	private const int IOCTL_SPOTUSB_VENDOR_ID = 5;
	private const int IOCTL_SPOTUSB_PRODUCT_ID = 6;
	private const int IOCTL_SPOTUSB_DISPLAY_NAME = 7;
	private const int IOCTL_SPOTUSB_PORT_NAME = 8;
	// paths
	static readonly string SpotGuidKeyPath = @"System\CurrentControlSet\Services\SpotUsb\Parameters";
	// discovery keys
	static public readonly string InquiriesInterface = "InquiriesInterface";
	static public readonly string DriverVersion = "DriverVersion";
	// mandatory property keys
	static public readonly string DeviceHash = "DeviceHash";
	static public readonly string DisplayName = "DisplayName";
	// optional property keys
	static public readonly string Manufacturer = "Manufacturer";
	static public readonly string Product = "Product";
	static public readonly string SerialNumber = "SerialNumber";
	static public readonly string VendorId = "VendorId";
	static public readonly string ProductId = "ProductId";
	private const int c_DeviceStringBufferSize = 260;
	static private Hashtable s_textProperties;
	static private Hashtable s_digitProperties;
	static private UsbDevice device = null;
	static private UsbEndpointReader reader = null;
	static private UsbEndpointWriter writer = null;
	static private Queue<byte[]> reciveQueue;

	static AsyncUsbStream ()
	{
		s_textProperties = new Hashtable ();
		s_digitProperties = new Hashtable ();

		s_textProperties.Add (DeviceHash, IOCTL_SPOTUSB_DEVICE_HASH);
		s_textProperties.Add (Manufacturer, IOCTL_SPOTUSB_MANUFACTURER);
		s_textProperties.Add (Product, IOCTL_SPOTUSB_PRODUCT);
		s_textProperties.Add (SerialNumber, IOCTL_SPOTUSB_SERIAL_NUMBER);  

		s_digitProperties.Add (VendorId, IOCTL_SPOTUSB_VENDOR_ID);
		s_digitProperties.Add (ProductId, IOCTL_SPOTUSB_PRODUCT_ID);   
	}

	public AsyncUsbStream (String port)
	{
		Console.WriteLine ("Create AsyncUSB");
		int vid = Convert.ToInt16 (port.Split (new char[]{ ':' }) [0]);
		int pid = Convert.ToInt16 (port.Split (new char[]{ ':' }) [1]);



		UsbDeviceFinder netduinoFinder = new UsbDeviceFinder (vid, pid);

		if (device == null)
			device = UsbDevice.OpenUsbDevice (netduinoFinder);

		if (device == null)
		{
			Console.WriteLine ("Cant open USB Device");
		}



		if (device.IsOpen)
		{
			if (writer == null)
			{
				reciveQueue = new Queue<byte[]> ();
				UsbConfigInfo configInfo = device.Configs [0];
				UsbInterfaceInfo interfaceInfo = configInfo.InterfaceInfoList [0];

				IUsbDevice wholeUsbDevice = device as IUsbDevice;
				if (!ReferenceEquals (wholeUsbDevice, null))
				{
					// This is a "whole" USB device. Before it can be used, 
					// the desired configuration and interface must be selected.

					// Select config #1
					wholeUsbDevice.SetConfiguration (configInfo.Descriptor.ConfigID);

					// Claim interface #0.
					wholeUsbDevice.ClaimInterface (interfaceInfo.Descriptor.InterfaceID);
				}

				UsbEndpointInfo EP1 = interfaceInfo.EndpointInfoList [1];
				writer = device.OpenEndpointWriter ((WriteEndpointID)EP1.Descriptor.EndpointID);

				UsbEndpointInfo EP2 = interfaceInfo.EndpointInfoList [0];
				reader = device.OpenEndpointReader ((ReadEndpointID)EP2.Descriptor.EndpointID);
				reader.DataReceived += new EventHandler<EndpointDataEventArgs> (OnDataReceive);
				reader.DataReceivedEnabled = true;
			}
		}

	}

	[MethodImpl(MethodImplOptions.Synchronized)]
	private void OnDataReceive (object sender, EndpointDataEventArgs e)
	{
		Console.WriteLine ("Receive << " + Encoding.Default.GetString (e.Buffer, 0, e.Count));
		Console.WriteLine (reciveQueue.Count);
		byte[] temp = new byte[e.Count];
		Array.Copy (e.Buffer, temp, e.Count);
		reciveQueue.Enqueue (temp);
	}

	~AsyncUsbStream ()
	{

	}

	public static PortDefinition[] EnumeratePorts ()
	{
		SortedList lst = new SortedList ();

		EnumeratePorts (new Guid (), "2", lst); 

		ICollection col = lst.Values;
		PortDefinition[] res = new PortDefinition[col.Count];

		col.CopyTo (res, 0);
		return res;
	}

	private static void EnumeratePorts (Guid inquiriesInterface, string driverVersion, SortedList lst)
	{
		UsbRegDeviceList allDevices = UsbDevice.AllDevices;
		UsbDeviceFinder netduinoFinder = new UsbDeviceFinder (8881, 4097);
		UsbRegistry usbRegistry = allDevices.Find (netduinoFinder);
		UsbDevice tempDevice;
		if (usbRegistry.Open (out tempDevice))
		{

			AsyncUsbStream s = null;

			s = new AsyncUsbStream ("8881:4097");

			string displayName = s.RetrieveStringFromDevice (IOCTL_SPOTUSB_DISPLAY_NAME); 
			string hash = s.RetrieveStringFromDevice (IOCTL_SPOTUSB_DEVICE_HASH); 
			string operationalPort = s.RetrieveStringFromDevice (IOCTL_SPOTUSB_PORT_NAME); 

			if (!((operationalPort == null) || (displayName == null) || (hash == null)))
			{
				PortDefinition pd = PortDefinition.CreateInstanceForUsb (displayName, operationalPort.ToString ());
				RetrieveProperties (hash, ref pd, s);
				lst.Add (pd.DisplayName, pd);
			}

		}

	}

	private static void RetrieveProperties (string hash, ref PortDefinition pd, AsyncUsbStream s)
	{
		IDictionaryEnumerator dict;

		dict = s_textProperties.GetEnumerator ();
		while (dict.MoveNext ())
		{
			pd.Properties.Add (dict.Key, s.RetrieveStringFromDevice ((int)dict.Value));
		}

		dict = s_digitProperties.GetEnumerator ();
		while (dict.MoveNext ())
		{
			pd.Properties.Add (dict.Key, s.RetrieveIntegerFromDevice ((int)dict.Value));
		}
	}

	private unsafe int RetrieveIntegerFromDevice (int controlCode)
	{

		switch (controlCode)
		{
			case IOCTL_SPOTUSB_VENDOR_ID:
				return 8881;
			case IOCTL_SPOTUSB_PRODUCT_ID:
				return 4097;
		}

		return 0;
	}

	private string RetrieveStringFromDevice (int controlCode)
	{

		switch (controlCode)
		{
			case IOCTL_SPOTUSB_DISPLAY_NAME:
				return device.Info.ProductString;
			case IOCTL_SPOTUSB_DEVICE_HASH:
				return string.Format ("{0}:{1}", 8881, 4097);
			case IOCTL_SPOTUSB_PORT_NAME:
				return string.Format ("{0}:{1}", 8881, 4097);
			case IOCTL_SPOTUSB_MANUFACTURER:
				return device.Info.ManufacturerString;
			case IOCTL_SPOTUSB_PRODUCT:
				return device.Info.ProductString;
			case IOCTL_SPOTUSB_SERIAL_NUMBER:
				return device.Info.SerialString;
		}

		return "";
	}

	public override bool CanRead
	{
		get { return true; }
	}

	public override bool CanSeek
	{
		get { return false; }
	}

	public override bool CanWrite
	{
		get { return true; }
	}

	public override long Length
	{
		get { throw NotImplemented (); }
	}

	public override long Position
	{
		get { throw NotImplemented (); }
		set { throw NotImplemented (); }
	}

	private Exception NotImplemented ()
	{
		return new NotSupportedException ("Not Supported");
	}

	public override void Flush ()
	{
		throw NotImplemented ();
	}

	[MethodImpl(MethodImplOptions.Synchronized)]
	public override int Read (byte[] buffer, int offset, int count)
	{

		byte[] tempb = reciveQueue.Dequeue ();
		Array.Copy (tempb, buffer, tempb.Length);

		return tempb.Length;

	}

	public override long Seek (long offset, SeekOrigin origin)
	{
		throw NotImplemented ();
	}

	public override void SetLength (long value)
	{
		throw NotImplemented ();
	}

	public override void Write (byte[] buffer, int offset, int count)
	{
		try
		{

			ErrorCode ec = ErrorCode.None;
			// If this is a "whole" usb device (libusb-win32, linux libusb)
			// it will have an IUsbDevice interface. If not (WinUSB) the 
			// variable will be null indicating this is an interface of a 
			// device.

			int bytesWritten;

			ec = writer.Write (buffer, 1000, out bytesWritten);
			Console.WriteLine ("Send >> " + Encoding.Default.GetString (buffer, 0, bytesWritten));
			if (ec != ErrorCode.None)
			{
				throw new Exception (UsbDevice.LastErrorString);
			}


		} catch (Exception e)
		{
			Console.WriteLine (e.Message);
		}



	}


	public virtual int AvailableCharacters
	{
		get
		{
			lock (reciveQueue)
			{
				if (reciveQueue.Count > 0)
				{
					Console.WriteLine ("Number of bytes " + reciveQueue.Peek ().Length);
					return reciveQueue.Peek ().Length;
				} else
				{
					return 0;
				}
			}

		}
	}
}

    [Serializable]
    public class PortDefinition_Usb : PortDefinition
    {
        public PortDefinition_Usb( string displayName, string port, ListDictionary properties ) : base(displayName, port)
        {
            m_properties = properties;
        }

        public override object UniqueId
        {
            get
            {
                return m_properties[AsyncUsbStream.DeviceHash];
            }
        }

        public override Stream CreateStream()
        {
            throw new NotImplementedException();

#if REMOVED_CODE
            try
            {
                return new AsyncUsbStream( m_port );
            }
            catch
            {
                object uniqueId = UniqueId;

                foreach(PortDefinition pd in AsyncUsbStream.EnumeratePorts())
                {
                    if(Object.Equals( pd.UniqueId, uniqueId ))
                    {
                        m_properties = pd.Properties;
                        m_port       = pd.Port;

                        return new AsyncUsbStream( m_port );
                    }
                }

                throw;
            }
#endif
        }    
    }

    [Serializable]
    public class PortDefinition_Tcp : PortDefinition
    {
        public const int WellKnownPort = 26000;

        IPEndPoint m_ipEndPoint;

        string m_macAddress = "";

        internal unsafe struct SOCK_discoveryinfo 
        {
            internal uint       ipaddr;
            internal uint       macAddressLen;
            internal fixed byte macAddressBuffer[64];    
        };

        public string MacAddress
        {
            get { return m_macAddress; }
                
        }
        

        public PortDefinition_Tcp(IPEndPoint ipEndPoint, string macAddress)
            : base(ipEndPoint.Address.ToString(), ipEndPoint.ToString())
        {
            if(!string.IsNullOrEmpty(macAddress))
            {
                m_displayName += " - (" + macAddress + ")";
            }
            m_ipEndPoint = ipEndPoint;
            m_macAddress = macAddress;
        }

        public PortDefinition_Tcp(IPEndPoint ipEndPoint)
            : this(ipEndPoint, "")
        {
            m_ipEndPoint = ipEndPoint;
        }

        public PortDefinition_Tcp(IPAddress address)
            : this(new IPEndPoint(address, WellKnownPort), "")
        {
        }

        public PortDefinition_Tcp(IPAddress address, string macAddress)
            : this(new IPEndPoint(address, WellKnownPort), macAddress)
        {
        }

        public override object UniqueId
        {
            get
            {
                return m_ipEndPoint.ToString();
            }
        }

        public static PortDefinition[] EnumeratePorts()
        {
            return EnumeratePorts(System.Net.IPAddress.Parse("234.102.98.44"), System.Net.IPAddress.Parse("234.102.98.45"), 26001, "DOTNETMF", 3000, 1);
        }

        public static PortDefinition[] EnumeratePorts(
            System.Net.IPAddress DiscoveryMulticastAddress    ,
            System.Net.IPAddress DiscoveryMulticastAddressRecv,
            int       DiscoveryMulticastPort       ,
            string    DiscoveryMulticastToken      ,
            int       DiscoveryMulticastTimeout    ,
            int       DiscoveryTTL                 
        )
        {
            PortDefinition_Tcp []ports = null;
            Dictionary<string, string> addresses = new Dictionary<string, string>();
            
            try
            {
                IPHostEntry hostEntry = Dns.GetHostEntry(Dns.GetHostName());

                foreach (IPAddress ip in hostEntry.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        int cnt = 0;
                        int total = 0;
                        byte[] data = new byte[1024];
                        Socket sock = null;
                        Socket recv = null;

                        System.Net.IPEndPoint endPoint    = new System.Net.IPEndPoint(ip, 0);
                        System.Net.EndPoint   epRemote    = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 26001);
                        System.Net.IPEndPoint epRecv      = new System.Net.IPEndPoint(ip, DiscoveryMulticastPort);
                        System.Net.IPEndPoint epMulticast = new System.Net.IPEndPoint(DiscoveryMulticastAddress, DiscoveryMulticastPort);

                        try
                        {
                            sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                            recv = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                            recv.Bind(epRecv);
                            recv.ReceiveTimeout = DiscoveryMulticastTimeout;
                            recv.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(DiscoveryMulticastAddressRecv, ip));

                            sock.Bind(endPoint);
                            sock.MulticastLoopback = false;
                            sock.Ttl = (short)DiscoveryTTL;
                            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 64);

                            // send ping
                            sock.SendTo(System.Text.Encoding.ASCII.GetBytes(DiscoveryMulticastToken), SocketFlags.None, epMulticast);

                            while (0 < (cnt = recv.ReceiveFrom(data, total, data.Length - total, SocketFlags.None, ref epRemote)))
                            {
                                addresses[((IPEndPoint)epRemote).Address.ToString()] = "";
                                total += cnt;
                                recv.ReceiveTimeout = DiscoveryMulticastTimeout / 2;
                            }

                            recv.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(DiscoveryMulticastAddressRecv));

                        }
                        // SocketException occurs in RecieveFrom if there is no data.
                        catch (SocketException)
                        {
                        }
                        finally
                        {
                            if (recv != null)
                            {
                                recv.Close();
                                recv = null;
                            }
                            if (sock != null)
                            {
                                sock.Close();
                                sock = null;
                            }
                        }

                        // use this if we need to get the MAC address of the device
                        SOCK_discoveryinfo disc = new SOCK_discoveryinfo();
                        disc.ipaddr = 0;
                        disc.macAddressLen = 0;
                        int idx = 0;
                        int c_DiscSize = Marshal.SizeOf(disc);
                        while (total >= c_DiscSize)
                        {
                            byte[] discData = new byte[c_DiscSize];
                            Array.Copy(data, idx, discData, 0, c_DiscSize);
                            GCHandle gch = GCHandle.Alloc(discData, GCHandleType.Pinned);
                            disc = (SOCK_discoveryinfo)Marshal.PtrToStructure(gch.AddrOfPinnedObject(), typeof(SOCK_discoveryinfo));
                            gch.Free();

                            // previously we only displayed the IP address for the device, which doesn't
                            // really tell you which device you are talking to.  The MAC address should be unique.
                            // therefore we will display the MAC address in the device display name to help distinguish
                            // the devices.  
                            if (disc.macAddressLen <= 64 && disc.macAddressLen > 0)
                            {
                                IPAddress ipResp = new IPAddress((long)disc.ipaddr);

                                // only append the MAC if it matches one of the IP address we got responses from
                                if (addresses.ContainsKey(ipResp.ToString()))
                                {
                                    string strMac = "";
                                    for (int mi = 0; mi < disc.macAddressLen - 1; mi++)
                                    {
                                        unsafe
                                        {
                                            strMac += string.Format("{0:x02}-", disc.macAddressBuffer[mi]);
                                        }
                                    }
                                    unsafe
                                    {
                                        strMac += string.Format("{0:x02}", disc.macAddressBuffer[disc.macAddressLen - 1]);
                                    }

                                    addresses[ipResp.ToString()] = strMac;
                                }
                            }
                            total -= c_DiscSize;
                            idx += c_DiscSize;
                        }
                    }
                }
            }
            catch( Exception e2)
            {
                System.Diagnostics.Debug.Print(e2.ToString());
            }

            ports = new PortDefinition_Tcp[addresses.Count];
            int i = 0;

            foreach(string key in addresses.Keys)
            {
                ports[i++] = new PortDefinition_Tcp(IPAddress.Parse(key), addresses[key]);
            }

            return ports;            
        }

        [MethodImplAttribute(MethodImplOptions.Synchronized)]
        public override Stream CreateStream()
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            socket.NoDelay = true;
            socket.LingerState = new LingerOption(false, 0);

            IAsyncResult asyncResult = socket.BeginConnect(m_ipEndPoint, null, null);

            if (asyncResult.AsyncWaitHandle.WaitOne(2000, false))
            {
                socket.EndConnect(asyncResult);
            }
            else
            {
                socket.Close();
                throw new IOException("Connect failed");
            }

            AsyncNetworkStream stream = new AsyncNetworkStream(socket, true);

            return stream;
        }    
    }
}
