﻿using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using Obd2Net.Configuration;
using Obd2Net.InfrastructureContracts;
using Obd2Net.InfrastructureContracts.Enums;
using Obd2Net.InfrastructureContracts.Protocols;

namespace Obd2Net.Ports
{
    internal class Elm327 : IPort
    {
        private readonly ILogger _logger;
        private SerialPort _serialPort;
        private readonly object _syncLock = new object();

        public Elm327(ILogger logger, IOBDConfiguration config) : this(logger, null, config)
        {
        }

        public Elm327(ILogger logger, IProtocol protocol, IOBDConfiguration config)
        {
            _logger = logger;
            Config = config;
            Protocol = protocol;
        }

        public IProtocol Protocol { get; private set; }

        public IEnumerable<ECU> Ecus => Protocol.EcuMap.Values;

        public IOBDConfiguration Config { get; }

        public OBDStatus Status { get; private set; }

        /// <summary>
        ///     Resets the device, and sets all attributes to unconnected states.
        /// </summary>
        public void Close()
        {
            lock (_syncLock)
            {
                Status = OBDStatus.NotConnected;
                Protocol = null;

                if (_serialPort != null)
                {
                    Write("ATZ");
                    _serialPort.Close();
                    _serialPort = null;
                }
            }
        }

        public IMessage[] SendAndParse(string cmd)
        {
            lock (_syncLock)
            {
                if (Status == OBDStatus.NotConnected)
                {
                    _logger.Debug("cannot send_and_parse() when unconnected");
                    return null;
                }

                var lines = Send(cmd);
                var messages = Protocol.Parse(lines);
                return messages;
            }
        }

        public string[] Send(string cmd, TimeSpan? delay = null)
        {
            lock (_syncLock)
            {
                Write(cmd);

                if (delay.HasValue)
                {
                    _logger.Debug($"wait: {delay.Value.TotalMilliseconds} Milliseconds");
                    Thread.Sleep(delay.Value);
                }

                return Read();
            }
        }

        public bool Connect()
        {
            if (string.IsNullOrWhiteSpace(Config.Portname))
                return ScanPortsAndConnect();

            return ConnectToSerialPort(Config.Portname, Config.Baudrate);
        }

        private bool ConnectToSerialPort(string portname, int baudrate)
        {
            lock (_syncLock)
            {
                _serialPort = new SerialPort(portname, baudrate);
                var timeout = Convert.ToInt32(Config.Timeout.TotalMilliseconds);
                // ------------- open port -------------
                try
                {
                    _logger.Debug($"Opening serial port '{Config.Portname}'");
                    _serialPort = new SerialPort(Config.Portname, Config.Baudrate, Parity.None)
                    {
                        StopBits = StopBits.One,
                        ReadTimeout = timeout,
                        WriteTimeout = timeout
                    };
                    _serialPort.Open();
                    _logger.Debug($"Serial port successfully opened on {Config.Portname}");
                }
                catch (Exception e)
                {
                    Error(e.Message);
                    return false;
                }

                // ---------------------------- ATZ (reset) ----------------------------
                try
                {
                    Send("ATZ", TimeSpan.FromSeconds(1)); // wait 1 second for ELM to initialize
                }
                catch (Exception e)
                {
                    Error(e.Message);
                    return false;
                }

                // -------------------------- ATE0 (echo OFF) --------------------------
                var r = Send("ATE0");
                if (!IsOk(r, true))
                {
                    Error("ATE0 did not return 'OK'");
                    return false;
                }

                // ------------------------- ATH1 (headers ON) -------------------------
                r = Send("ATH1");
                if (!IsOk(r))
                {
                    Error("ATH1 did not return 'OK', or echoing is still ON");
                    return false;
                }
                // ------------------------ ATL0 (linefeeds OFF) -----------------------
                r = Send("ATL0");
                if (!IsOk(r))
                {
                    Error("ATL0 did not return 'OK'");
                    return false;
                }

                // by now, we've successfuly communicated with the ELM, but not the car
                Status = OBDStatus.ElmConnected;

                // try to communicate with the car, and load the correct protocol parser
                if (CheckProtocol())
                {
                    Status = OBDStatus.CarConnected;
                    _logger.Info("Connection successful");
                    return true;
                }

                _logger.Info("Connected to the adapter, but failed to connect to the vehicle");
                return false;
            }
        }

        private bool ScanPortsAndConnect()
        {
            lock (_syncLock)
            {
                if (string.IsNullOrWhiteSpace(Config.Portname))
                {
                    _logger.Debug("Using scan_serial to select port");
                    var portnames = SerialPort.GetPortNames();
                    _logger.Debug("Available ports: " + string.Join(",", portnames));

                    if (!portnames.Any())
                    {
                        _logger.Debug("No OBD-II adapters found");
                        return false;
                    }

                    foreach (var p in portnames)
                    {
                        _logger.Debug($"Attempting to use port: {p}");
                        if (ConnectToSerialPort(p, Config.Baudrate))
                            return true; // Connected
                    }
                }
                return false;
            }
        }

        private bool CheckProtocol()
        {
            lock (_syncLock)
            {
                if (Protocol != null)
                {
                    Send($"ATTP{Protocol.ElmId}");
                    var r0100 = Send("0100");

                    if (r0100.Any(m => m.Contains("UNABLE TO CONNECT"))) return false;

                    Protocol.PopulateEcuMap(r0100);
                    return true;
                }
                else
                {
                    
                }
            }
            return false;
        }

        private bool IsOk(string[] lines, bool expectEcho = false)
        {
            if (lines == null || lines.Length == 0)
                return false;

            if (expectEcho)
            {
                // don't test for the echo itself
                // allow the adapter to already have echo disabled
                return lines.Any(l => l.Contains("OK"));
            }

            return lines.Length == 1 && lines[0] == "OK";
        }

        private void Error(string msg)
        {
            Close();

            _logger.Debug("Connection Error:");
            if (!string.IsNullOrWhiteSpace(msg))
                _logger.Debug(msg);
        }

        /// <summary>
        ///     "low-level" function to write a string to the port
        /// </summary>
        /// <param name="cmd"></param>
        private void Write(string cmd)
        {
            lock (_syncLock)
            {
                if (_serialPort != null)
                {
                    cmd += "\r\n"; // terminate
                    _logger.Debug("write: " + cmd);
                    _serialPort.DiscardInBuffer(); // dump everything in the input buffer
                    var buffer = Utils.GetBytes(cmd);
                    _serialPort.Write(buffer, 0, buffer.Length); // turn the string into bytes and write
                }
                else
                    _logger.Debug("cannot perform Write() when unconnected");
            }
        }

        /// <summary>
        ///     low-level read function - accumulates characters until the prompt character is seen returns a list of [/r/n]
        ///     delimited strings
        /// </summary>
        private string[] Read()
        {
            lock (_syncLock)
            {
                var attempts = 2;
                var buffer = new List<byte>();

                if (_serialPort != null)
                    while (true)
                    {
                        byte? c = null;
                        try
                        {
                            c = (byte) _serialPort.ReadChar();
                        }
                        catch (TimeoutException)
                        {
                        }
                        if (!c.HasValue)
                        {
                            if (attempts <= 0)
                            {
                                _logger.Debug("Failed to read port, giving up");
                                break;
                            }

                            _logger.Debug("Failed to read port, trying again...");
                            attempts -= 1;
                            continue;
                        }
                        
                        if (c == '>') // end on chevron (ELM prompt character)
                            break;

                        if (c == '\x00') // skip null characters (ELM spec page 9)
                            continue;

                        buffer.Add(c.Value); // whatever is left must be part of the response
                    }
                else
                {
                    _logger.Debug("cannot perform Read() when unconnected");
                    return new string[0];
                }

                _logger.Debug($"read: {buffer.Count} bytes");

                // convert bytes into a standard string
                var raw = Utils.GetString(buffer.ToArray());

                // splits into lines
                // removes empty lines
                // removes trailing spaces
                return raw.Split(new[] {"\r", "\n"}, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        public void Dispose()
        {
            lock (_syncLock)
            {
                if (_serialPort?.IsOpen ?? false)
                    _serialPort.Close();

                _serialPort = null;
            }
        }
    }
}