﻿// Copyright (c) 2012-2017 fo-dicom contributors.
// Licensed under the Microsoft Public License (MS-PL).

#if !NET35

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Dicom.Log;

namespace Dicom.Network
{
    /// <summary>
    /// General client class for DICOM services.
    /// </summary>
    public class DicomClient
    {
        #region FIELDS

        private const int DefaultAssociationTimeout = 5000;

        private const int DefaultReleaseTimeout = 10000;

        private readonly object _lock = new object();

        private readonly AsyncManualResetEvent<bool> _associationFlag;

        private readonly AsyncManualResetEvent<Exception> _completionFlag;

        private readonly List<DicomRequest> _requests;

        private DicomServiceUser _service;

        private Task _serviceRunnerTask;

        private int _asyncInvoked;

        private int _asyncPerformed;

        private INetworkStream _networkStream;

        private bool _aborted;

        private Logger _logger;

        #endregion

        #region CONSTRUCTORS

        /// <summary>
        /// Initializes an instance of <see cref="DicomClient"/>.
        /// </summary>
        public DicomClient()
        {
            _requests = new List<DicomRequest>();
            AdditionalPresentationContexts = new List<DicomPresentationContext>();
            _asyncInvoked = 1;
            _asyncPerformed = 1;
            Linger = 50;

            _associationFlag = new AsyncManualResetEvent<bool>();
            _completionFlag = new AsyncManualResetEvent<Exception>();
        }

        #endregion

        #region DELEGATES

        /// <summary>
        /// Delegate for client handling the C-STORE request immediately.
        /// </summary>
        /// <param name="request">C-STORE request subject to handling.</param>
        /// <returns>Response from handling the C-STORE <paramref name="request"/>.</returns>
        public delegate DicomCStoreResponse CStoreRequestHandler(DicomCStoreRequest request);

        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets or sets the handler of a client C-STORE request.
        /// </summary>
        public CStoreRequestHandler OnCStoreRequest { get; set; }

        /// <summary>
        /// Gets or sets time in milliseconds to keep connection alive for additional requests.
        /// </summary>
        public int Linger { get; set; }

        /// <summary>
        /// Gets or sets the fallback encoding.
        /// </summary>
        public Encoding FallbackEncoding { get; set; }

        /// <summary>
        /// Gets or sets logger that is passed to the underlying <see cref="DicomService"/> implementation.
        /// </summary>
        public Logger Logger
        {
            get
            {
                return _logger ?? (_logger = LogManager.GetLogger("Dicom.Network"));
            }
            set
            {
                _logger = value;
            }
        }

        /// <summary>
        /// Gets or sets options to control behavior of <see cref="DicomService"/> base class.
        /// </summary>
        public DicomServiceOptions Options { get; set; }

        /// <summary>
        /// Gets or sets additional presentation contexts to negotiate with association.
        /// </summary>
        public List<DicomPresentationContext> AdditionalPresentationContexts { get; set; }

        /// <summary>
        /// Gets whether send functionality is activated or not.
        /// </summary>
        public bool CanSend
        {
            get
            {
                int requestsCount;
                lock (_lock)
                {
                    requestsCount = _requests.Count;
                }

                return requestsCount > 0 || AdditionalPresentationContexts.Count > 0;
            }
        }

        /// <summary>
        /// Gets whether a new send invocation is required. Send needs to be called if there are requests in queue and client is not connected.
        /// </summary>
        public bool IsSendRequired
        {
            get
            {
                bool required;
                lock (_lock)
                {
                    required = _requests.Count > 0 && (_service == null || !_service.IsConnected);
                }

                return required;
            }
        }

        #endregion

        #region METHODS

        /// <summary>
        /// Set negotiation asynchronous operations.
        /// </summary>
        /// <param name="invoked">Asynchronous operations invoked.</param>
        /// <param name="performed">Asynchronous operations performed.</param>
        public void NegotiateAsyncOps(int invoked = 0, int performed = 0)
        {
            _asyncInvoked = invoked;
            _asyncPerformed = performed;
        }

        /// <summary>
        /// Add DICOM service request.
        /// </summary>
        /// <param name="request">DICOM request.</param>
        public void AddRequest(DicomRequest request)
        {
            lock (_lock)
            {
                _requests.Add(request);
            }
        }

        /// <summary>
        /// Synchonously send existing requests to DICOM service.
        /// </summary>
        /// <param name="host">DICOM host.</param>
        /// <param name="port">Port.</param>
        /// <param name="useTls">True if TLS security should be enabled, false otherwise.</param>
        /// <param name="callingAe">Calling Application Entity Title.</param>
        /// <param name="calledAe">Called Application Entity Title.</param>
        /// <param name="millisecondsTimeout">Timeout in milliseconds for establishing association.</param>
        public void Send(string host, int port, bool useTls, string callingAe, string calledAe,
            int millisecondsTimeout = DefaultAssociationTimeout)
        {
            if (!CanSend) return;

            var noDelay = Options?.TcpNoDelay ?? DicomServiceOptions.Default.TcpNoDelay;
            var ignoreSslPolicyErrors = Options?.IgnoreSslPolicyErrors
                                        ?? DicomServiceOptions.Default.IgnoreSslPolicyErrors;

            _networkStream = NetworkManager.CreateNetworkStream(host, port, useTls, noDelay, ignoreSslPolicyErrors);

            var assoc = new DicomAssociation(callingAe, calledAe)
            {
                MaxAsyncOpsInvoked = _asyncInvoked,
                MaxAsyncOpsPerformed = _asyncPerformed,
                RemoteHost = host,
                RemotePort = port
            };

            try
            {
                SendAsync(_networkStream, assoc, millisecondsTimeout).Wait();
            }
            catch (AggregateException e)
            {
                // ReSharper disable once PossibleNullReferenceException
                throw e.Flatten().InnerException;
            }
        }

        /// <summary>
        /// Asynchonously send existing requests to DICOM service.
        /// </summary>
        /// <param name="host">DICOM host.</param>
        /// <param name="port">Port.</param>
        /// <param name="useTls">True if TLS security should be enabled, false otherwise.</param>
        /// <param name="callingAe">Calling Application Entity Title.</param>
        /// <param name="calledAe">Called Application Entity Title.</param>
        /// <param name="millisecondsTimeout">Timeout in milliseconds for establishing association.</param>
        /// <returns>Awaitable task.</returns>
        public Task SendAsync(string host, int port, bool useTls, string callingAe, string calledAe,
            int millisecondsTimeout = DefaultAssociationTimeout)
        {
            if (!CanSend) Task.FromResult(false); // TODO Replace with Task.CompletedTask when moving to .NET 4.6

            var noDelay = Options?.TcpNoDelay ?? DicomServiceOptions.Default.TcpNoDelay;
            var ignoreSslPolicyErrors = Options?.IgnoreSslPolicyErrors
                                        ?? DicomServiceOptions.Default.IgnoreSslPolicyErrors;

            _networkStream = NetworkManager.CreateNetworkStream(host, port, useTls, noDelay, ignoreSslPolicyErrors);

            var assoc = new DicomAssociation(callingAe, calledAe)
            {
                MaxAsyncOpsInvoked = _asyncInvoked,
                MaxAsyncOpsPerformed = _asyncPerformed,
                RemoteHost = host,
                RemotePort = port
            };

            return SendAsync(_networkStream, assoc, millisecondsTimeout);
        }

        /// <summary>
        /// Synchronously send existing requests to DICOM service.
        /// </summary>
        /// <param name="stream">Established network stream.</param>
        /// <param name="callingAe">Calling Application Entity Title.</param>
        /// <param name="calledAe">Called Application Entity Title.</param>
        /// <param name="millisecondsTimeout">Timeout in milliseconds for establishing association.</param>
        public void Send(INetworkStream stream, string callingAe, string calledAe,
            int millisecondsTimeout = DefaultAssociationTimeout)
        {
            if (!CanSend) return;

            var assoc = new DicomAssociation(callingAe, calledAe)
            {
                MaxAsyncOpsInvoked = _asyncInvoked,
                MaxAsyncOpsPerformed = _asyncPerformed,
                RemoteHost = stream.RemoteHost,
                RemotePort = stream.RemotePort
            };

            try
            {
                SendAsync(stream, assoc, millisecondsTimeout).Wait();
            }
            catch (AggregateException e)
            {
                // ReSharper disable once PossibleNullReferenceException
                throw e.Flatten().InnerException;
            }
        }

        /// <summary>
        /// Asynchronously send existing requests to DICOM service.
        /// </summary>
        /// <param name="stream">Established network stream.</param>
        /// <param name="callingAe">Calling Application Entity Title.</param>
        /// <param name="calledAe">Called Application Entity Title.</param>
        /// <param name="millisecondsTimeout">Timeout in milliseconds for establishing association.</param>
        /// <returns>Awaitable task.</returns>
        public Task SendAsync(INetworkStream stream, string callingAe, string calledAe,
            int millisecondsTimeout = DefaultAssociationTimeout)
        {
            if (!CanSend) return Task.FromResult(false); // TODO Replace with Task.CompletedTask when moving to .NET 4.6

            var assoc = new DicomAssociation(callingAe, calledAe)
            {
                MaxAsyncOpsInvoked = _asyncInvoked,
                MaxAsyncOpsPerformed = _asyncPerformed,
                RemoteHost = stream.RemoteHost,
                RemotePort = stream.RemotePort
            };

            return SendAsync(stream, assoc, millisecondsTimeout);
        }

        /// <summary>
        /// Synchronously wait for association.
        /// </summary>
        /// <param name="millisecondsTimeout">Milliseconds to wait for association to occur.</param>
        /// <returns>True if association is established, false otherwise.</returns>
        [Obsolete("Use AssociationAccepted and AssociationRejected events to be notified of association status change.")]
        public bool WaitForAssociation(int millisecondsTimeout = DefaultAssociationTimeout)
        {
            try
            {
                return WaitForAssociationAsync(millisecondsTimeout).Result;
            }
            catch (AggregateException e)
            {
                // ReSharper disable once PossibleNullReferenceException
                throw e.Flatten().InnerException;
            }
        }

        /// <summary>
        /// Asynchronously wait for association.
        /// </summary>
        /// <param name="millisecondsTimeout">Milliseconds to wait for association to occur.</param>
        /// <returns>True if association is established, false otherwise.</returns>
        [Obsolete(
            "Use AssociationAccepted and AssociationRejected events to be notified of association status change.")]
        public async Task<bool> WaitForAssociationAsync(int millisecondsTimeout = DefaultAssociationTimeout)
        {
            try
            {
                var timedOut = false;
                using (var cancellationSource = new CancellationTokenSource(millisecondsTimeout))
                using (cancellationSource.Token.Register(() =>
                {
                    _associationFlag.Set(false);
                    _completionFlag.Set(null);
                    timedOut = true;
                }, false))
                {
                    return await _associationFlag.WaitAsync().ConfigureAwait(false) && !timedOut;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Synchronously release association.
        /// </summary>
        public void Release(int millisecondsTimeout = DefaultReleaseTimeout)
        {
            try
            {
                ReleaseAsync(millisecondsTimeout).Wait();
            }
            catch (AggregateException e)
            {
                // ReSharper disable once PossibleNullReferenceException
                throw e.Flatten().InnerException;
            }
        }

        /// <summary>
        /// Asynchronously release association.
        /// </summary>
        /// <returns></returns>
        public async Task ReleaseAsync(int millisecondsTimeout = DefaultReleaseTimeout)
        {
            try
            {
                if (_service != null)
                {
                    await _service.DoSendAssociationReleaseRequestAsync(millisecondsTimeout).ConfigureAwait(false);
                }
            }
            finally
            {
                await HandleMonitoredExceptionsAsync(true).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Abort DICOM service connection.
        /// </summary>
        public void Abort()
        {
            try
            {
                AbortAsync().Wait();
            }
            catch (AggregateException e)
            {
                // ReSharper disable once PossibleNullReferenceException
                throw e.Flatten().InnerException;
            }
        }

        /// <summary>
        /// Asynchronously abort DICOM service connection.
        /// </summary>
        public async Task AbortAsync()
        {
            if (_aborted) return;

            try
            {
                if (_service != null)
                {
                    await _service.DoSendAbortAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _aborted = true;
                await HandleMonitoredExceptionsAsync(true).ConfigureAwait(false);
            }
        }

        private async Task SendAsync(INetworkStream stream, DicomAssociation association, int millisecondsTimeout)
        {
            try
            {
                if (_service == null || !_service.IsConnected)
                {
                    _associationFlag.Reset();
                    _completionFlag.Reset();

                    _service = new DicomServiceUser(this, stream, association, Options, FallbackEncoding, Logger);
                    _serviceRunnerTask = _service.RunAsync();
                }

                await Task.WhenAny(_serviceRunnerTask, DoSendAsync(millisecondsTimeout)).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.Error("Failed to send due to: {@error}", e);
                throw;
            }
            finally
            {
                await HandleMonitoredExceptionsAsync(false).ConfigureAwait(false);
            }
        }

        private async Task DoSendAsync(int millisecondsTimeout)
        {
#pragma warning disable 618
            var associated = await WaitForAssociationAsync(millisecondsTimeout).ConfigureAwait(false);
#pragma warning restore 618

            bool send;
            lock (_lock)
            {
                send = associated && _requests.Count > 0;
            }

            if (send)
            {
                IList<DicomRequest> copy;
                lock (_lock)
                {
                    copy = new List<DicomRequest>(_requests);
                    _requests.Clear();
                }

                foreach (var request in copy)
                {
                    await _service.SendRequestAsync(request).ConfigureAwait(false);
                }
            }
            else if (associated)
            {
                await _service.DoSendAssociationReleaseRequestAsync(millisecondsTimeout).ConfigureAwait(false);
            }

            await _completionFlag.WaitAsync().ConfigureAwait(false);
        }

        private async Task HandleMonitoredExceptionsAsync(bool cleanup)
        {
            var completedException = await _completionFlag.WaitAsync().ConfigureAwait(false);
            var lingerException = _service?.LingerTask?.Exception;

            if (cleanup || completedException != null || lingerException != null)
            {
                if (_networkStream != null)
                {
                    try
                    {
                        _networkStream.Dispose();
                    }
                    catch (Exception e)
                    {
                        Logger.Warn("Failed to dispose network stream, reason: {@error}", e);
                    }

                    _networkStream = null;
                }

                if (_service != null)
                {
                    try
                    {
                        _service.Dispose();
                    }
                    catch (Exception e)
                    {
                        Logger.Warn("Failed to dispose service, reason: {@error}", e);
                    }

                    _service = null;
                }
            }

            // If not already set, set association notifier here to signal completion to awaiters
            _associationFlag.Set(false);

            if (completedException != null)
            {
                throw completedException;
            }

            if (lingerException != null)
            {
                // ReSharper disable once PossibleNullReferenceException
                throw lingerException.Flatten().InnerException;
            }
        }

        #endregion

        #region INNER TYPES

        private class DicomServiceUser : DicomService, IDicomServiceUser
        {
            #region FIELDS

            private readonly DicomClient _client;

            private readonly DicomAssociation _association;

            private bool _isInitialized;

            private readonly object _lock = new object();

            #endregion

            #region CONSTRUCTORS

            internal DicomServiceUser(
                DicomClient client,
                INetworkStream stream,
                DicomAssociation association,
                DicomServiceOptions options,
                Encoding fallbackEncoding,
                Logger log)
                : base(stream, fallbackEncoding, log)
            {
                _client = client;

                if (options != null)
                {
                    Options = options;
                }

                List<DicomRequest> requests;
                lock (_client._lock)
                {
                    requests = new List<DicomRequest>(_client._requests);
                }

                foreach (var request in requests)
                {
                    association.PresentationContexts.AddFromRequest(request);
                }

                foreach (var context in client.AdditionalPresentationContexts)
                {
                    association.PresentationContexts.Add(
                        context.AbstractSyntax,
                        context.UserRole,
                        context.ProviderRole,
                        context.GetTransferSyntaxes().ToArray());
                }

                _association = association;
                _isInitialized = false;
            }

            #endregion

            #region PROPERTIES

            internal Task LingerTask { get; private set; }

            #endregion

            #region METHODS

            /// <summary>
            /// Callback for handling association accept scenarios.
            /// </summary>
            /// <param name="association">Accepted association.</param>
            public void OnReceiveAssociationAccept(DicomAssociation association)
            {
                foreach (var ctx in _client.AdditionalPresentationContexts)
                {
                    foreach (var item in
                        association.PresentationContexts.Where(pc => pc.AbstractSyntax == ctx.AbstractSyntax))
                    {
                        ctx.SetResult(item.Result, item.AcceptedTransferSyntax);
                    }
                }

                SetAssociationFlag(true);
            }

            /// <summary>
            /// Callback for handling association reject scenarios.
            /// </summary>
            /// <param name="result">Specification of rejection result.</param>
            /// <param name="source">Source of rejection.</param>
            /// <param name="reason">Detailed reason for rejection.</param>
            public void OnReceiveAssociationReject(
                DicomRejectResult result,
                DicomRejectSource source,
                DicomRejectReason reason)
            {
                SetAssociationFlag(false);
                SetCompletionFlag(new DicomAssociationRejectedException(result, source, reason));
            }

            /// <summary>
            /// Callback on response from an association release.
            /// </summary>
            public void OnReceiveAssociationReleaseResponse()
            {
                SetCompletionFlag();
            }

            /// <summary>
            /// Setup long-running operations that the DICOM service manages.
            /// </summary>
            /// <returns>Awaitable task maintaining the long-running operation(s).</returns>
            public override Task RunAsync()
            {
                if (_isInitialized) return Task.FromResult(false);
                _isInitialized = true;

                return Task.WhenAll(base.RunAsync(), SendAssociationRequestAsync(_association));
            }

            /// <summary>
            /// Callback on recieving an abort message.
            /// </summary>
            /// <param name="source">Abort source.</param>
            /// <param name="reason">Detailed reason for abort.</param>
            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
            {
                SetCompletionFlag(new DicomAssociationAbortedException(source, reason));
            }

            /// <summary>
            /// Callback when connection is closed.
            /// </summary>
            /// <param name="exception">Exception, if any, that forced connection to close.</param>
            public void OnConnectionClosed(Exception exception)
            {
                SetCompletionFlag(exception);
            }

            /// <summary>
            /// Callback for handling a client related C-STORE request, typically emanating from the client's C-GET request.
            /// </summary>
            /// <param name="request">
            /// C-STORE request.
            /// </param>
            /// <returns>
            /// The <see cref="DicomCStoreResponse"/> related to the C-STORE <paramref name="request"/>.
            /// </returns>
            public DicomCStoreResponse OnCStoreRequest(DicomCStoreRequest request)
            {
                return _client.OnCStoreRequest == null
                           ? new DicomCStoreResponse(request, DicomStatus.StorageStorageOutOfResources)
                           : _client.OnCStoreRequest(request);
            }

            internal async Task DoSendAssociationReleaseRequestAsync(int millisecondsTimeout)
            {
                Exception exception = null;
                try
                {
                    await Task.WhenAny(SendAssociationReleaseRequestAsync(),
                        ListenForDisconnectAsync(millisecondsTimeout, true)).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Logger.Warn("Attempt to send association release request failed due to: {@error}", e);
                    exception = e;
                }
                finally
                {
                    SetCompletionFlag(exception);
                }
            }

            internal async Task DoSendAbortAsync()
            {
                if (IsConnected)
                {
                    await SendAbortAsync(DicomAbortSource.ServiceUser, DicomAbortReason.NotSpecified)
                        .ConfigureAwait(false);
                    SetCompletionFlag();
                }
            }

            /// <summary>
            /// Action to perform when send queue is empty.
            /// </summary>
            protected override void OnSendQueueEmpty()
            {
                lock (_lock)
                {
                    if (LingerTask == null || LingerTask.IsCompleted)
                    {
                        LingerTask = LingerAsync();
                    }
                }
            }

            private async Task LingerAsync()
            {
                while (true)
                {
                    var disconnected = await ListenForDisconnectAsync(_client.Linger, false).ConfigureAwait(false);

                    if (disconnected)
                    {
                        SetCompletionFlag();
                        break;
                    }

                    if (IsSendQueueEmpty)
                    {
                        await DoSendAssociationReleaseRequestAsync(DefaultReleaseTimeout).ConfigureAwait(false);
                        break;
                    }
                }
            }

            private async Task<bool> ListenForDisconnectAsync(int millisecondsDelay, bool releaseRequested)
            {
                try
                {
                    using (var cancellationSource = new CancellationTokenSource(millisecondsDelay))
                    {
                        while (IsConnected)
                        {
                            await Task.Delay(1, cancellationSource.Token).ConfigureAwait(false);

                            if (IsConnected && !releaseRequested)
                            {
                                IList<DicomRequest> copy;

                                lock (_client._lock)
                                {
                                    copy = new List<DicomRequest>(_client._requests);
                                    _client._requests.Clear();
                                }

                                foreach (var request in copy)
                                {
                                    await SendRequestAsync(request).ConfigureAwait(false);
                                }
                            }
                        }

                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            private void SetAssociationFlag(bool isAssociated)
            {
                _client._associationFlag.Set(isAssociated);
            }

            private void SetCompletionFlag(Exception exception = null)
            {
                _client._completionFlag.Set(exception);
            }

            #endregion
        }

        #endregion
    }
}

#endif
