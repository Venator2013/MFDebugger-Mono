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
using System.Diagnostics;

namespace Microsoft.SPOT.Debugger
{
	// NOTE: this is a serial stream compatible with Mono
	public class AsyncSerialStream : System.IO.Stream, IDisposable, WireProtocol.IStreamAvailableCharacters
	{
		private SerialPort _serialPort = null;
		private Thread _serialPortThread = null;
		bool _disposing = false;
		bool _disposed = false;
		private object _writeLock = new object ();

		public AsyncSerialStream (string port, uint baudrate)
		{
			_serialPort = new SerialPort (port, (int)baudrate, Parity.None, 8, StopBits.One);
			_serialPort.Handshake = Handshake.None;
			_serialPort.ReadTimeout = System.Threading.Timeout.Infinite;
			_serialPort.Open ();
		}

		~AsyncSerialStream ()
		{
			Dispose (false);
		}

		protected override void Dispose (bool disposing)
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
					_serialPort.Dispose ();
				}
			}

			_disposed = true;
			
			base.Dispose (disposing);
		}

		public override void Close ()
		{
			Dispose (true);
		}

		#region IDisposable Members

		void IDisposable.Dispose ()
		{
			base.Dispose (true);

			Dispose (true);

			GC.SuppressFinalize (this);
		}

		#endregion

		public void ConfigureXonXoff (bool fEnable)
		{
			//TODO: implement this function
			throw new NotImplementedException (); 
		}

		static public PortDefinition[] EnumeratePorts ()
		{
			SortedList lst = new SortedList ();

			string[] portNames = System.IO.Ports.SerialPort.GetPortNames ();
			for (int iPortName = 0; iPortName < portNames.Length; iPortName++)
			{
				lst.Add (portNames [iPortName], PortDefinition.CreateInstanceForSerial (portNames [iPortName], portNames [iPortName], 115200));
			}
			
			/* NOTE: on Mac OS X, the SerialPort.GetPortNames() function does not return any values.
			 *       For now (on Mac OS X), it is necessary to manually add serial port entries. 
			 *       Here are some sample serial port definition entries. */
			//lst.Add("/dev/tty.KeySerial1", PortDefinition.CreateInstanceForSerial("/dev/tty.KeySerial1", "/dev/tty.KeySerial1", 115200));
			//lst.Add("/dev/ttyUSB0", PortDefinition.CreateInstanceForSerial("/dev/ttyUSB0", "/dev/ttyUSB0", 115200));
			//lst.Add("COM2", PortDefinition.CreateInstanceForSerial("COM2", "COM2", 115200));

			ICollection col = lst.Values;
			PortDefinition[] res = new PortDefinition[col.Count];

			col.CopyTo (res, 0);

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
			get { throw NotImplemented (); }
		}

		public override long Position
		{
			get { throw NotImplemented (); }
			set { throw NotImplemented (); }
		}

		public override void Flush ()
		{
			_serialPort.BaseStream.Flush ();
		}

		public override int Read (byte[] buffer, int offset, int count)
		{
			IAsyncResult result = _serialPort.BaseStream.BeginRead (buffer, offset, count, null, null);
			result.AsyncWaitHandle.WaitOne ();
			return _serialPort.BaseStream.EndRead (result);
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
			// THIS IS CRITICAL: ONLY ONE WRITER AT A TIME!
			lock (_writeLock)
			{
				for (int i = offset; i < offset + count; i++)
				{
					IAsyncResult result = _serialPort.BaseStream.BeginWrite (buffer, i, 1, null, null);
					while (!result.IsCompleted)
						result.AsyncWaitHandle.WaitOne ();
					
					_serialPort.BaseStream.EndWrite (result);
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

		private Exception NotImplemented ()
		{
			return new NotSupportedException ("Not Supported");
		}
	}

	public class AsyncNetworkStream : NetworkStream, WireProtocol.IStreamAvailableCharacters
	{
		public AsyncNetworkStream (Socket socket, bool ownsSocket)
            : base (socket, ownsSocket)
		{
		}

		protected override void Dispose (bool disposing)
		{
			base.Dispose (disposing);
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

		public PortDefinition_Serial (string displayName, string port, uint baudRate) : base (displayName, port)
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

		public override Stream CreateStream ()
		{
			return new AsyncSerialStream (m_port, m_baudRate);
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
				UsbDevice.Exit();
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

		static private UsbDevice device = null;
		static private UsbEndpointReader reader = null;
		static private UsbEndpointWriter writer = null;
		private byte m_claimedInterface;
		static private Queue<byte[]> receiveQueue = new Queue<byte[]> ();

		#region static member

		public static PortDefinition[] EnumeratePorts ()
		{
			SortedList lst = new SortedList ();

			EnumeratePorts ( lst); 

			ICollection col = lst.Values;
			PortDefinition[] res = new PortDefinition[col.Count];

			col.CopyTo (res, 0);
			return res;
		}

		private static void EnumeratePorts ( SortedList lst)
		{
			UsbRegDeviceList allDevices = UsbDevice.AllDevices;
			UsbDevice tempDevice;

			foreach (UsbRegistry usbRegistry in allDevices)
			{
				if (usbRegistry.Open (out tempDevice))
				{

					if (!tempDevice.Info.Descriptor.Class.Equals (LibUsbDotNet.Descriptors.ClassCodeType.PerInterface))
					{
						continue;
					}

					string displayName = usbRegistry.FullName;
					string operationalPort = usbRegistry.Vid.ToString () + ":" + usbRegistry.Pid.ToString (); 

					if (!((operationalPort == null) || (displayName == null)))
					{
						PortDefinition pd = PortDefinition.CreateInstanceForUsb (displayName, operationalPort.ToString ());
						lst.Add (pd.DisplayName, pd);
					}
				}
			}

		}

		#endregion

		public AsyncUsbStream (String port)
		{
			int vid = Convert.ToInt16 (port.Split (new char[]{ ':' }) [0]);
			int pid = Convert.ToInt16 (port.Split (new char[]{ ':' }) [1]);

			UsbDeviceFinder netduinoFinder = new UsbDeviceFinder (vid, pid);

			if (device == null)
				device = UsbDevice.OpenUsbDevice (netduinoFinder);

			if (device == null)
			{
				Console.WriteLine ("Cant open USB Device");
			
			} else
			{
				if (device.IsOpen)
				{
					if (writer == null)
					{
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
							m_claimedInterface = interfaceInfo.Descriptor.InterfaceID;
						}
						
						foreach (UsbEndpointInfo ep in interfaceInfo.EndpointInfoList)
						{
							if ((ep.Descriptor.EndpointID & 0x80) != 0x80)
							{
								writer = device.OpenEndpointWriter ((WriteEndpointID)ep.Descriptor.EndpointID);


							} else
							{

								reader = device.OpenEndpointReader ((ReadEndpointID)ep.Descriptor.EndpointID);
								reader.DataReceived += (OnDataReceive);
								reader.DataReceivedEnabled = true;
							}

						}
					}
				}
			}

		}

		~AsyncUsbStream ()
		{
			//Dispose (false);
		}

		protected override void Dispose (bool disposing)
		{

			//consistent cleanup if we are disposed or finalized.

			lock (this)
			{
				if (device != null && device.IsOpen)
				{

					reader.DataReceivedEnabled = false;
					reader.DataReceived -= (OnDataReceive);

					IUsbDevice wholeUsbDevice = device as IUsbDevice;
					if (!ReferenceEquals (wholeUsbDevice, null))
					{
						// This is a "whole" USB device. Before it can be used, 
						// the desired configuration and interface must be selected.

						// Release interface #1
						wholeUsbDevice.ReleaseInterface (m_claimedInterface);

					}

					device.Close ();
				}
				device = null;
			}
				

			base.Dispose (disposing);
		}

		public override void Close ()
		{
			//Dispose (true);
		}

		#region IDisposable Members

		void IDisposable.Dispose ()
		{
			base.Dispose (true);

			//Dispose (true);

			GC.SuppressFinalize (this);
		}

		#endregion
		[MethodImpl(MethodImplOptions.Synchronized)]
		private void OnDataReceive (object sender, EndpointDataEventArgs e)
		{
			Console.WriteLine ("Receive ( "+e.Count+" bytes ) << " + Encoding.Default.GetString (e.Buffer, 0, e.Count));

			byte[] temp = new byte[e.Count];
			Array.Copy (e.Buffer, temp, e.Count);
			receiveQueue.Enqueue (temp);
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

		[MethodImpl (MethodImplOptions.Synchronized)]
		public override int Read (byte[] buffer, int offset, int count)
		{

			byte[] tempb = receiveQueue.Dequeue ();
			Console.WriteLine (Encoding.Default.GetString (tempb, 0, tempb.Length));
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

				int bytesWritten;

				ec = writer.Write(buffer, 1000, out bytesWritten);

				Console.WriteLine ("Send ( "+ bytesWritten +" bytes ) >> " + Encoding.Default.GetString (buffer, 0, bytesWritten));
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
				lock (receiveQueue)
				{
					if (receiveQueue.Count > 0)
					{
						return receiveQueue.Peek ().Length;
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
		public PortDefinition_Usb (string displayName, string port, ListDictionary ld) : base (displayName, port)
		{
		}

		public override object UniqueId
		{
			get
			{
				return m_port + ":" + m_displayName;
			}
		}

		public override Stream CreateStream ()
		{
            
			try
			{
				return new AsyncUsbStream (m_port);
			} catch
			{
				object uniqueId = UniqueId;

				foreach (PortDefinition pd in AsyncUsbStream.EnumeratePorts())
				{
					if (Object.Equals (pd.UniqueId, uniqueId))
					{
						m_properties = pd.Properties;
						m_port = pd.Port;

						return new AsyncUsbStream (m_port);
					}
				}

				throw;
			}
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
			internal uint ipaddr;
			internal uint macAddressLen;
			internal fixed byte macAddressBuffer[64];
		};

		public string MacAddress
		{
			get { return m_macAddress; }
                
		}

		public PortDefinition_Tcp (IPEndPoint ipEndPoint, string macAddress)
            : base (ipEndPoint.Address.ToString (), ipEndPoint.ToString ())
		{
			if (!string.IsNullOrEmpty (macAddress))
			{
				m_displayName += " - (" + macAddress + ")";
			}
			m_ipEndPoint = ipEndPoint;
			m_macAddress = macAddress;
		}

		public PortDefinition_Tcp (IPEndPoint ipEndPoint)
            : this (ipEndPoint, "")
		{
			m_ipEndPoint = ipEndPoint;
		}

		public PortDefinition_Tcp (IPAddress address)
            : this (new IPEndPoint (address, WellKnownPort), "")
		{
		}

		public PortDefinition_Tcp (IPAddress address, string macAddress)
            : this (new IPEndPoint (address, WellKnownPort), macAddress)
		{
		}

		public override object UniqueId
		{
			get
			{
				return m_ipEndPoint.ToString ();
			}
		}

		public static PortDefinition[] EnumeratePorts ()
		{
			return EnumeratePorts (System.Net.IPAddress.Parse ("234.102.98.44"), System.Net.IPAddress.Parse ("234.102.98.45"), 26001, "DOTNETMF", 3000, 1);
		}

		public static PortDefinition[] EnumeratePorts (
			System.Net.IPAddress DiscoveryMulticastAddress,
			System.Net.IPAddress DiscoveryMulticastAddressRecv,
			int       DiscoveryMulticastPort,
			string    DiscoveryMulticastToken,
			int       DiscoveryMulticastTimeout,
			int       DiscoveryTTL                 
		)
		{
			PortDefinition_Tcp[] ports = null;
			Dictionary<string, string> addresses = new Dictionary<string, string> ();
            
			try
			{
				IPHostEntry hostEntry = Dns.GetHostEntry (Dns.GetHostName ());

				foreach (IPAddress ip in hostEntry.AddressList)
				{
					if (ip.AddressFamily == AddressFamily.InterNetwork)
					{
						int cnt = 0;
						int total = 0;
						byte[] data = new byte[1024];
						Socket sock = null;
						Socket recv = null;

						System.Net.IPEndPoint endPoint = new System.Net.IPEndPoint (ip, 0);
						System.Net.EndPoint epRemote = new System.Net.IPEndPoint (System.Net.IPAddress.Any, 26001);
						System.Net.IPEndPoint epRecv = new System.Net.IPEndPoint (ip, DiscoveryMulticastPort);
						System.Net.IPEndPoint epMulticast = new System.Net.IPEndPoint (DiscoveryMulticastAddress, DiscoveryMulticastPort);

						try
						{
							sock = new Socket (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
							recv = new Socket (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

							recv.Bind (epRecv);
							recv.ReceiveTimeout = DiscoveryMulticastTimeout;
							recv.SetSocketOption (SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption (DiscoveryMulticastAddressRecv, ip));

							sock.Bind (endPoint);
							sock.MulticastLoopback = false;
							sock.Ttl = (short)DiscoveryTTL;
							sock.SetSocketOption (SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 64);

							// send ping
							sock.SendTo (System.Text.Encoding.ASCII.GetBytes (DiscoveryMulticastToken), SocketFlags.None, epMulticast);

							while (0 < (cnt = recv.ReceiveFrom (data, total, data.Length - total, SocketFlags.None, ref epRemote)))
							{
								addresses [((IPEndPoint)epRemote).Address.ToString ()] = "";
								total += cnt;
								recv.ReceiveTimeout = DiscoveryMulticastTimeout / 2;
							}

							recv.SetSocketOption (SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption (DiscoveryMulticastAddressRecv));

						}
                        // SocketException occurs in RecieveFrom if there is no data.
                        catch (SocketException)
						{
						} finally
						{
							if (recv != null)
							{
								recv.Close ();
								recv = null;
							}
							if (sock != null)
							{
								sock.Close ();
								sock = null;
							}
						}

						// use this if we need to get the MAC address of the device
						SOCK_discoveryinfo disc = new SOCK_discoveryinfo ();
						disc.ipaddr = 0;
						disc.macAddressLen = 0;
						int idx = 0;
						int c_DiscSize = Marshal.SizeOf (disc);
						while (total >= c_DiscSize)
						{
							byte[] discData = new byte[c_DiscSize];
							Array.Copy (data, idx, discData, 0, c_DiscSize);
							GCHandle gch = GCHandle.Alloc (discData, GCHandleType.Pinned);
							disc = (SOCK_discoveryinfo)Marshal.PtrToStructure (gch.AddrOfPinnedObject (), typeof(SOCK_discoveryinfo));
							gch.Free ();

							// previously we only displayed the IP address for the device, which doesn't
							// really tell you which device you are talking to.  The MAC address should be unique.
							// therefore we will display the MAC address in the device display name to help distinguish
							// the devices.  
							if (disc.macAddressLen <= 64 && disc.macAddressLen > 0)
							{
								IPAddress ipResp = new IPAddress ((long)disc.ipaddr);

								// only append the MAC if it matches one of the IP address we got responses from
								if (addresses.ContainsKey (ipResp.ToString ()))
								{
									string strMac = "";
									for (int mi = 0; mi < disc.macAddressLen - 1; mi++)
									{
										unsafe
										{
											strMac += string.Format ("{0:x02}-", disc.macAddressBuffer [mi]);
										}
									}
									unsafe
									{
										strMac += string.Format ("{0:x02}", disc.macAddressBuffer [disc.macAddressLen - 1]);
									}

									addresses [ipResp.ToString ()] = strMac;
								}
							}
							total -= c_DiscSize;
							idx += c_DiscSize;
						}
					}
				}
			} catch (Exception e2)
			{
				System.Diagnostics.Debug.Print (e2.ToString ());
			}

			ports = new PortDefinition_Tcp[addresses.Count];
			int i = 0;

			foreach (string key in addresses.Keys)
			{
				ports [i++] = new PortDefinition_Tcp (IPAddress.Parse (key), addresses [key]);
			}

			return ports;            
		}

		[MethodImplAttribute (MethodImplOptions.Synchronized)]
		public override Stream CreateStream ()
		{
			Socket socket = new Socket (AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

			socket.NoDelay = true;
			socket.LingerState = new LingerOption (false, 0);

			IAsyncResult asyncResult = socket.BeginConnect (m_ipEndPoint, null, null);

			if (asyncResult.AsyncWaitHandle.WaitOne (2000, false))
			{
				socket.EndConnect (asyncResult);
			} else
			{
				socket.Close ();
				throw new IOException ("Connect failed");
			}

			AsyncNetworkStream stream = new AsyncNetworkStream (socket, true);

			return stream;
		}
	}
}
