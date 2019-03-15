﻿// *********************************************************************
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License
// *********************************************************************
using System;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.StreamProcessing.Internal.Collections;

namespace Microsoft.StreamProcessing
{
    [DataContract]
    internal sealed class UnionPipe<TKey, TPayload> : BinaryPipe<TKey, TPayload, TPayload, TPayload>
    {
        private readonly MemoryPool<TKey, TPayload> pool;
        private readonly string errorMessages;

        [DataMember]
        private StreamMessage<TKey, TPayload> output;

        [DataMember]
        private long nextLeftTime = long.MinValue;
        [DataMember]
        private long nextRightTime = long.MinValue;

        [Obsolete("Used only by serialization. Do not call directly.")]
        public UnionPipe() { }

        public UnionPipe(Streamable<TKey, TPayload> stream, IStreamObserver<TKey, TPayload> observer)
            : base(stream, observer)
        {
            this.errorMessages = stream.ErrorMessages;
            this.pool = MemoryManager.GetMemoryPool<TKey, TPayload>(stream.Properties.IsColumnar);
            this.pool.Get(out this.output);
            this.output.Allocate();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void DisposeState()
        {
            this.output.Free();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void ProcessBothBatches(StreamMessage<TKey, TPayload> leftBatch, StreamMessage<TKey, TPayload> rightBatch, out bool leftBatchDone, out bool rightBatchDone, out bool leftBatchFree, out bool rightBatchFree)
        {
            leftBatchFree = rightBatchFree = true;

            long lastLeftTime = -1;
            long lastRightTime = -1;

            bool first = (leftBatch.iter == 0);
            if (!GoToVisibleRow(leftBatch))
            {
                leftBatchDone = true;
                rightBatchDone = false;
                return;
            }

            this.nextLeftTime = leftBatch.vsync.col[leftBatch.iter];
            if (first) lastLeftTime = MaxBatchSyncTime(leftBatch);

            first = (rightBatch.iter == 0);
            if (!GoToVisibleRow(rightBatch))
            {
                leftBatchDone = false;
                rightBatchDone = true;

                return;
            }

            this.nextRightTime = rightBatch.vsync.col[rightBatch.iter];
            if (first) lastRightTime = MaxBatchSyncTime(rightBatch);

            if ((lastLeftTime != -1) && (lastRightTime != -1))
            {
                leftBatchDone = rightBatchDone = false;
                if (lastLeftTime <= this.nextRightTime)
                {
                    OutputBatch(leftBatch);
                    leftBatchDone = true;
                    leftBatchFree = false;
                }

                if (Config.DeterministicWithinTimestamp ? (lastRightTime < this.nextLeftTime) : (lastRightTime <= this.nextLeftTime))
                {
                    OutputBatch(rightBatch);
                    rightBatchDone = true;
                    rightBatchFree = false;
                }

                if (leftBatchDone || rightBatchDone) return;
            }

            while (true)
            {
                if (this.nextLeftTime <= this.nextRightTime)
                {
                    OutputCurrentTuple(leftBatch);

                    leftBatch.iter++;

                    if (!GoToVisibleRow(leftBatch))
                    {
                        leftBatchDone = true;
                        rightBatchDone = false;
                        return;
                    }

                    this.nextLeftTime = leftBatch.vsync.col[leftBatch.iter];
                }
                else
                {
                    OutputCurrentTuple(rightBatch);

                    rightBatch.iter++;

                    if (!GoToVisibleRow(rightBatch))
                    {
                        leftBatchDone = false;
                        rightBatchDone = true;
                        return;
                    }

                    this.nextRightTime = rightBatch.vsync.col[rightBatch.iter];
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void ProcessLeftBatch(StreamMessage<TKey, TPayload> batch, out bool isBatchDone, out bool isBatchFree)
        {
            isBatchFree = true;
            if (batch.iter == 0)
            {
                long maxLeftTime = MaxBatchSyncTime(batch);
                if (maxLeftTime <= nextRightTime)
                {
                    OutputBatch(batch);
                    isBatchDone = true;
                    isBatchFree = false;
                    return;
                }
            }

            while (true)
            {
                if (!GoToVisibleRow(batch))
                {
                    isBatchDone = true;
                    return;
                }

                this.nextLeftTime = batch.vsync.col[batch.iter];

                if (this.nextLeftTime > this.nextRightTime)
                {
                    isBatchDone = false;
                    return;
                }

                OutputCurrentTuple(batch);

                batch.iter++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void ProcessRightBatch(StreamMessage<TKey, TPayload> batch, out bool isBatchDone, out bool isBatchFree)
        {
            isBatchFree = true;
            if (batch.iter == 0)
            {
                long maxRightTime = MaxBatchSyncTime(batch);
                if (Config.DeterministicWithinTimestamp ? (maxRightTime < nextLeftTime) : (maxRightTime <= nextLeftTime))
                {
                    OutputBatch(batch);
                    isBatchDone = true;
                    isBatchFree = false;
                    return;
                }
            }

            while (true)
            {
                if (!GoToVisibleRow(batch))
                {
                    isBatchDone = true;
                    return;
                }

                this.nextRightTime = batch.vsync.col[batch.iter];

                if (this.nextRightTime >= this.nextLeftTime)
                {
                    isBatchDone = false;
                    return;
                }

                OutputCurrentTuple(batch);

                batch.iter++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool GoToVisibleRow(StreamMessage<TKey, TPayload> batch)
        {
            while (batch.iter < batch.Count && (batch.bitvector.col[batch.iter >> 6] & (1L << (batch.iter & 0x3f))) != 0 && batch.vother.col[batch.iter] >= 0)
                batch.iter++;

            return (batch.iter != batch.Count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long MaxBatchSyncTime(StreamMessage<TKey, TPayload> batch)
        {
            // Punctuations are not necessarily monotonically increasing with respect to data events, so we cannot
            // simply retrieve the last event.
            // Iterate from end of batch until we hit a data event, then take the maximum of the events we have seen.
            long max = -1;
            for (int i = batch.Count - 1; i >= 0; i--)
            {
                // Skip deleted data events
                if ((batch.bitvector.col[i >> 6] & (1L << (i & 0x3f))) != 0 && batch.vother.col[i] >= 0)
                {
                    continue;
                }

                max = Math.Max(max, batch.vsync.col[i]);
                if (batch.vother.col[i] != StreamEvent.PunctuationOtherTime)
                {
                    break;
                }
            }

            return max;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OutputCurrentTuple(StreamMessage<TKey, TPayload> batch)
        {
            if (batch.vother.col[batch.iter] == StreamEvent.PunctuationOtherTime)
            {
                if (batch.vsync.col[batch.iter] <= this.lastCTI) return;

                this.lastCTI = batch.vsync.col[batch.iter];
            }

            int index = this.output.Count++;
            this.output.vsync.col[index] = batch.vsync.col[batch.iter];
            this.output.vother.col[index] = batch.vother.col[batch.iter];
            this.output.key.col[index] = batch.key.col[batch.iter];
            this.output[index] = batch[batch.iter];
            this.output.hash.col[index] = batch.hash.col[batch.iter];
            if ((batch.bitvector.col[batch.iter >> 6] & (1L << (batch.iter & 0x3f))) != 0) this.output.bitvector.col[index >> 6] |= (1L << (index & 0x3f));

            if (this.output.Count == Config.DataBatchSize) FlushContents();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OutputBatch(StreamMessage<TKey, TPayload> batch)
        {
            long updatedCTI = this.lastCTI;
            for (int i = 0; i < batch.Count; i++)
            {
                // Find first punctuation
                if (batch.vother.col[i] == StreamEvent.PunctuationOtherTime)
                {
                    if (batch.vsync.col[i] <= updatedCTI)
                    {
                        // Remove the redundant punctuation by converting to a deleted data event
                        batch.vother.col[i] = 0;
                        batch.bitvector.col[i >> 6] |= (1L << (i & 0x3f));
                    }
                    else
                        updatedCTI = batch.vsync.col[i];
                }
            }

            this.lastCTI = updatedCTI;

            FlushContents();
            this.Observer.OnNext(batch);
        }

        protected override void ProduceBinaryQueryPlan(PlanNode left, PlanNode right)
        {
            var node = new UnionPlanNode(
                left, right, this, typeof(TKey), typeof(TPayload), false, false, this.errorMessages, false);
            this.Observer.ProduceQueryPlan(node);
        }

        protected override void FlushContents()
        {
            if (this.output.Count == 0) return;
            this.output.Seal();
            this.Observer.OnNext(this.output);
            this.pool.Get(out this.output);
            this.output.Allocate();
        }

        public override int CurrentlyBufferedOutputCount => this.output.Count;
    }
}
