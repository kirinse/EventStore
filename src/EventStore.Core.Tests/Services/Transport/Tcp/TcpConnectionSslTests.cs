﻿using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using EventStore.Transport.Tcp;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.Tests.Services.Transport.Tcp {
	[TestFixture]
	public class TcpConnectionSslTests {
		protected static Socket CreateListeningSocket() {
			var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
			listener.Listen(1);
			return listener;
		}

		private IEnumerable<ArraySegment<byte>> GenerateData() {
			var data = new List<ArraySegment<byte>>();
			data.Add(new ArraySegment<byte>(new byte[100]));
			return data;
		}

		[Test]
		public async Task no_data_should_be_dispatched_after_tcp_connection_closed() {
			for (int i = 0; i < 1000; i++) {
				bool closed = false;
				bool dataReceivedAfterClose = false;
				var listeningSocket = CreateListeningSocket();

				var clientTcpConnection = TcpConnectionSsl.CreateConnectingConnection(
					Guid.NewGuid(),
					(IPEndPoint)listeningSocket.LocalEndPoint,
					"localhost",
					false,
					new TcpClientConnector(),
					TimeSpan.FromSeconds(5),
					(conn) => {
					},
					(conn, error) => {
						Assert.Fail($"Connection failed: {error}");
					},
					false);

				var serverSocket = listeningSocket.Accept();
				var serverTcpConnection = TcpConnectionSsl.CreateServerFromSocket(Guid.NewGuid(),
					(IPEndPoint)serverSocket.RemoteEndPoint, serverSocket, GetCertificate(), false);

				try {
					clientTcpConnection.ConnectionClosed += (connection, error) => {
						Volatile.Write(ref closed, true);
					};

					clientTcpConnection.ReceiveAsync((connection, data) => {
						if (Volatile.Read(ref closed)) {
							dataReceivedAfterClose = true;
						}
					});

					using (var b = new Barrier(2)) {
						Task sendData = Task.Factory.StartNew(() => {
							b.SignalAndWait();
							for (int i = 0; i < 1000; i++)
								serverTcpConnection.EnqueueSend(GenerateData());
						}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

						Task closeConnection = Task.Factory.StartNew(() => {
							b.SignalAndWait();
							serverTcpConnection.Close("Intentional close");
						}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

						await Task.WhenAll(sendData, closeConnection);
						Assert.False(dataReceivedAfterClose);
					}
				} finally {
					clientTcpConnection.Close("Shut down");
					serverTcpConnection.Close("Shut down");
					listeningSocket.Dispose();
				}
			}
		}

		private X509Certificate GetCertificate() {
			using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EventStore.Core.Tests.server.p12");
			using var mem = new MemoryStream();
			stream.CopyTo(mem);
			return new X509Certificate2(mem.ToArray(), "1111");
		}
	}
}
