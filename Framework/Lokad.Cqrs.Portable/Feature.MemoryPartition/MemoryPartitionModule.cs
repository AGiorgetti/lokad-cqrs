﻿#region (c) 2010-2011 Lokad - CQRS for Windows Azure - New BSD License 

// Copyright (c) Lokad 2010-2011, http://www.lokad.com
// This code is released as Open Source under the terms of the New BSD Licence

#endregion

using System;
using System.Threading;
using Lokad.Cqrs.Build.Engine;
using Lokad.Cqrs.Core.Dispatch;
using Lokad.Cqrs.Core.Dispatch.Events;
using Lokad.Cqrs.Core.Inbox.Events;
using Lokad.Cqrs.Core.Outbox;
using Lokad.Cqrs.Core;

namespace Lokad.Cqrs.Feature.MemoryPartition
{
    public sealed class MemoryPartitionModule : HideObjectMembersFromIntelliSense, IAdvancedDispatchBuilder
    {
        readonly string[] _memoryQueues;

        Func<Container,  Action<byte[]>> _dispatcher;
        IEnvelopeQuarantine _quarantine;

        public MemoryPartitionModule(string[] memoryQueues)
        {
            _memoryQueues = memoryQueues;
            Quarantine(new MemoryQuarantine());
        }

        public void DispatcherIs(Func<Container, ISingleThreadMessageDispatcher> factory)
        {
            DispatcherIsLambda(container =>
                {
                    var dis = factory(container);
                    dis.Init();
                    return (envelope => dis.DispatchMessage(envelope));
                });
        }

        /// <summary>
        /// Defines dispatcher as lambda method that is resolved against the container
        /// </summary>
        /// <param name="factory">The factory.</param>
        public void DispatcherIsLambda(HandlerFactory factory)
        {
            _dispatcher = container =>
                {
                    var d = factory(container);
                    var manager = container.Resolve<MessageDuplicationManager>();
                    var streamer = container.Resolve<IEnvelopeStreamer>();
                    var wrapper = new DispatchWrapper(d, _quarantine, manager, streamer);
                    return (buffer => wrapper.Dispatch(buffer));
                };
        }

        public void Quarantine(IEnvelopeQuarantine quarantine)
        {
            _quarantine = quarantine;
        }

        public void DispatchToRoute(Func<ImmutableEnvelope, string> route)
        {
            DispatcherIs(ctx => new DispatchMessagesToRoute(ctx.Resolve<QueueWriterRegistry>(), route, ctx.Resolve<IEnvelopeStreamer>()));
        }

        IEngineProcess BuildConsumingProcess(Container context)
        {
            var log = context.Resolve<ISystemObserver>();
            var dispatcher = _dispatcher(context);

            var account = context.Resolve<MemoryAccount>();
            var notifier = account.GetMemoryInbox(_memoryQueues);

            return new DispatcherProcess(log, dispatcher, notifier);
        }

        public void Configure(Container container)
        {
            if (null == _dispatcher)
            {
                throw new InvalidOperationException(@"No message dispatcher configured, please supply one.

You can use either 'DispatcherIsLambda' or reference Lokad.CQRS.Composite and 
use Command/Event dispatchers. If you are migrating from v2.0, that's what you 
should do.");
            }

            var process = BuildConsumingProcess(container);
            var setup = container.Resolve<EngineSetup>();
            setup.AddProcess(process);
        }
    }

    public sealed class DispatchWrapper
    {
        Action<ImmutableEnvelope> _action;
        IEnvelopeQuarantine _quarantine;
        MessageDuplicationMemory _manager;
        IEnvelopeStreamer _streamer;
        ISystemObserver _observer;

        public DispatchWrapper(Action<ImmutableEnvelope> action, IEnvelopeQuarantine quarantine, MessageDuplicationManager manager, IEnvelopeStreamer streamer)
        {
            _action = action;
            _quarantine = quarantine;
            _manager = manager.GetOrAdd(this);
            _streamer = streamer;
        }


        public void Dispatch(byte[] message)
        {

            ImmutableEnvelope envelope = null;
            try
            {
                envelope = _streamer.ReadAsEnvelopeData(message);
            }
            catch (Exception ex)
            {
                // permanent quarantine for serialization problems
                _quarantine.Quarantine(message, ex);
                _observer.Notify(new EnvelopeDeserializationFailed(ex,"dispatch"));
                return;
            }

            if (_manager.DoWeRemember(envelope.EnvelopeId))
            {
                _observer.Notify(new EnvelopeDuplicateDiscarded(envelope.EnvelopeId));
                return;
            }
                

            try
            {
                _action(envelope);
                _manager.Memorize(envelope.EnvelopeId);
                _quarantine.TryRelease(envelope);
            }
            catch (ThreadAbortException e)
            {
                return;

            }
            catch (Exception ex)
            {
                if (_quarantine.TryToQuarantine(envelope, ex))
                {
                    _observer.Notify(new EnvelopeQuarantined(ex, envelope));
                    // message quarantined. Swallow
                    return;
                }
                // if we are on a persistent queue, this will tell to retry
                throw;
            }
        }
    }
}