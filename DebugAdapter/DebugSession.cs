﻿// Original work by:
/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// Modified by:
/*---------------------------------------------------------------------------------------------
*  Copyright (c) NEXON Korea Corporation. All rights reserved.
*  Licensed under the MIT License. See License.txt in the project root for license information.
*--------------------------------------------------------------------------------------------*/

using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace VSCodeDebug
{
    public class DebugSession : ICDPListener, IDebugeeListener
    {
        public ICDPSender toVSCode;
        public IDebugeeSender toDebugee;
        private Process process;

        public DebugSession()
        {
            Program.WaitingUI.SetLabelText(
                "Waiting for commands from Visual Studio Code...");
        }

        void ICDPListener.FromVSCode(string command, int seq, dynamic args, string reqText)
        {
//            MessageBox.OK(reqText);

            if (args == null)
            {
                args = new { };
            }

            try
            {
                switch (command)
                {
                    case "initialize":
                        Initialize(command, seq, args);
                        break;

                    case "launch":
                        Launch(command, seq, args);
                        break;

                    case "attach":
                        Attach(command, seq, args);
                        break;

                    case "disconnect":
                        Disconnect(command, seq, args);
                        break;

                    case "next":
                    case "continue":
                    case "stepIn":
                    case "stepOut":
                    case "stackTrace":
                    case "scopes":
                    case "variables":
                    case "threads":
                    case "setBreakpoints":
                    case "configurationDone":
                        toDebugee.Send(reqText);
                        break;

                    case "pause":
                    case "evaluate":
                    case "source":
                        SendErrorResponse(command, seq, 1020, "command not supported: " + command);
                        break;

                    default:
                        SendErrorResponse(command, seq, 1014, "unrecognized request: {_request}", new { _request = command });
                        break;
                }
            }
            catch (Exception e)
            {
                MessageBox.WTF(e.ToString());
                SendErrorResponse(command, seq, 1104, "error while processing request '{_request}' (exception: {_exception})", new { _request = command, _exception = e.Message });
                Environment.Exit(1);
            }
        }

        void IDebugeeListener.FromDebuggee(byte[] json)
        {
            toVSCode.SendJSONEncodedMessage(json);
        }

        public void SendResponse(string command, int seq, dynamic body)
        {
            var response = new Response(command, seq);
            if (body != null)
            {
                response.SetBody(body);
            }
            toVSCode.SendMessage(response);
        }

        public void SendErrorResponse(string command, int seq, int id, string format, dynamic arguments = null, bool user = true, bool telemetry = false)
        {
            var response = new Response(command, seq);
            var msg = new Message(id, format, arguments, user, telemetry);
            var message = Utilities.ExpandVariables(msg.format, msg.variables);
            response.SetErrorBody(message, new ErrorResponseBody(msg));
            toVSCode.SendMessage(response);
        }

        void Disconnect(string command, int seq, dynamic arguments)
        {
            if (process != null)
            {
                try
                {
                    process.Kill();
                }
                catch(Exception)
                {
                    // 정상 종료하면 이쪽 경로로 들어온다.
                }
                process = null;
            }

            SendResponse(command, seq, null);
            toVSCode.Stop();
        }

        void Initialize(string command, int seq, dynamic args)
        {
            SendResponse(command, seq, new Capabilities()
            {
                supportsConfigurationDoneRequest = true,
                supportsFunctionBreakpoints = false,
                supportsConditionalBreakpoints = false,
                supportsEvaluateForHovers = false,
                exceptionBreakpointFilters = new dynamic[0]
            });
        }

        void Launch(string command, int seq, dynamic args)
        {
            string gprojPath = args.gprojPath;
            if (gprojPath == null)
            {
                //--------------------------------
                // validate argument 'executable'
                var runtimeExecutable = (string)args.executable;
                if (runtimeExecutable == null) { runtimeExecutable = ""; }

                runtimeExecutable = runtimeExecutable.Trim();
                if (runtimeExecutable.Length == 0)
                {
                    SendErrorResponse(command, seq, 3005, "Property 'executable' is empty.");
                    return;
                }
                if (!File.Exists(runtimeExecutable))
                {
                    SendErrorResponse(command, seq, 3006, "Runtime executable '{path}' does not exist.", new { path = runtimeExecutable });
                    return;
                }

                //--------------------------------
                // validate argument 'workingDirectory'
                var workingDirectory = ReadWorkingDirectory(command, seq, args);
                if (workingDirectory == null) { return; }

                //--------------------------------
                var arguments = (string)args.arguments;
                if (arguments == null) { arguments = ""; }

                process = new Process();
                process.StartInfo.CreateNoWindow = false;
                process.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.WorkingDirectory = workingDirectory;
                process.StartInfo.FileName = runtimeExecutable;
                process.StartInfo.Arguments = arguments;

                process.EnableRaisingEvents = true;
                process.Exited += (object sender, EventArgs e) =>
                {
                    toVSCode.SendMessage(new TerminatedEvent());
                };

                var cmd = string.Format("{0} {1}", runtimeExecutable, arguments);
                toVSCode.SendOutput("console", cmd);

                try
                {
                    process.Start();
                }
                catch (Exception e)
                {
                    SendErrorResponse(command, seq, 3012, "Can't launch terminal ({reason}).", new { reason = e.Message });
                    return;
                }
            }
            else
            {
                var toolkit = new VS2GiderosBridge.ToolKit(toVSCode);
                toolkit.GiderosPath = (string)args.giderosPath;
                toolkit.GprojPath = (string)args.gprojPath;

                new System.Threading.Thread(() => {
                    try
                    {
                        toolkit.Start();
                    }
                    catch (Exception e)
                    {
                        toVSCode.SendOutput("stderr", e.ToString());
                    }
                }).Start();
            }

            // 이후의 절차는 Attach랑 같다
            Attach(command, seq, args);
        }

        void Attach(string command, int seq, dynamic args)
        {
            var workingDirectory = ReadWorkingDirectory(command, seq, args);
            if (workingDirectory == null) { return; }

            IPAddress listenAddr = (bool)args.listenPublicly
                ? IPAddress.Any
                : IPAddress.Parse("127.0.0.1");
            int port = (int)args.listenPort;

            TcpListener listener = new TcpListener(listenAddr, port);
            listener.Start();
            Program.WaitingUI.SetLabelText(
                "Waiting for debugee at TCP " +
                listenAddr.ToString() + ":" +
                ((int)port).ToString() + "...");
            var clientSocket = listener.AcceptSocket(); // 여기서 블럭됨
            listener.Stop();
            Program.WaitingUI.Hide();
            var networkStream = new NetworkStream(clientSocket);
            var ncom = new NetworkCommunication(this, networkStream);
            this.toDebugee = ncom;

            var welcome = new {
                command = "welcome",
                sourceBasePath = workingDirectory
            };
            toDebugee.Send(JsonConvert.SerializeObject(welcome));

            ncom.StartThread();
            SendResponse(command, seq, null);

            toVSCode.SendMessage(new InitializedEvent());
        }

        string ReadWorkingDirectory(string command, int seq, dynamic args)
        {
            var workingDirectory = (string)args.workingDirectory;
            if (workingDirectory == null) { workingDirectory = ""; }

            workingDirectory = workingDirectory.Trim();
            if (workingDirectory.Length == 0)
            {
                SendErrorResponse(command, seq, 3003, "Property 'cwd' is empty.");
                return null;
            }
            if (!Directory.Exists(workingDirectory))
            {
                SendErrorResponse(command, seq, 3004, "Working directory '{path}' does not exist.", new { path = workingDirectory });
                return null;
            }

            return workingDirectory;
        }

        public void DebugeeHasGone()
        {
            toVSCode.SendMessage(new TerminatedEvent());
        }
    }
}