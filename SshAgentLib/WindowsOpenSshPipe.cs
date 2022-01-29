﻿// SPDX-License-Identifier: MIT
// Copyright (c) 2017,2022 David Lechner <david@lechnology.com>

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using ConnectionHandler = System.Action<System.IO.Stream, System.Diagnostics.Process>;

namespace dlech.SshAgentLib
{
    /// <summary>
    /// A named pipe server for Windows OpenSSH.
    /// </summary>
    public sealed class WindowsOpenSshPipe : IDisposable
    {
        private const string agentPipeId = "openssh-ssh-agent";
        private const int bufferSize = 5 * 1024;


        private readonly CancellationTokenSource cancelSource;
        private readonly Task listenerTask;

        /// <summary>
        /// Creates a new Windows OpenSSH Agent pipe.
        /// </summary>
        /// <param name="connectionHandler">
        /// A callback for handling client connections.
        /// </param>
        /// <exception cref="PageantRunningException">
        /// Thrown if the pipe file path is already in use.
        /// exception>
        public WindowsOpenSshPipe(ConnectionHandler connectionHandler)
        {
            if (File.Exists($"//./pipe/{agentPipeId}"))
            {
                throw new PageantRunningException();
            }

            cancelSource = new CancellationTokenSource();
            listenerTask = RunListenerAsync(connectionHandler, cancelSource.Token);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(
            IntPtr Pipe, out uint ClientProcessId);

        private static async Task RunListenerAsync(
            ConnectionHandler connectionHandler, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using (var server = new NamedPipeServerStream(
                    agentPipeId,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.WriteThrough | PipeOptions.Asynchronous,
                    bufferSize,
                    bufferSize))
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    if (!GetNamedPipeClientProcessId(
                        server.SafePipeHandle.DangerousGetHandle(), out var clientPid))
                    {
                        throw new IOException("Failed to get client PID", Marshal.GetHRForLastWin32Error());
                    }

                    var proc = Process.GetProcessById((int)clientPid);

                    using (cancellationToken.Register(() => server.Disconnect()))
                    {
                        await Task.Run(() => connectionHandler(server, proc)).ConfigureAwait(false);
                    }
                }
            }
        }

        public void Dispose()
        {
            // allow multiple calls to dispose
            if (listenerTask.IsCompleted)
            {
                return;
            }

            cancelSource.Cancel();

            try
            {
                listenerTask.Wait();
            }
            catch (AggregateException)
            {
                // expected since we just canceled the task
            }
        }
    }
}
