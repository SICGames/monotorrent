﻿//
// StreamingPieceRequester.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2021 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.Linq;

namespace MonoTorrent.Client.PiecePicking
{
    public class StreamingPieceRequester : IStreamingPieceRequester
    {
        bool RefreshAfterSeeking = false;

        ITorrentData TorrentData { get; set; }

        public bool InEndgameMode { get; private set; }

        public IPiecePicker Picker => LowPriorityPicker;

        IPiecePicker LowPriorityPicker { get; set; }

        IPiecePicker HighPriorityPicker { get; set; }


        /// <summary>
        /// This is the piece index of the block of data currently being consumed by the
        /// media player or other program.
        /// </summary>
        public int HighPriorityPieceIndex { get; private set; }

        /// <summary>
        /// The number of pieces which will be kept buffered to avoid stuttering while streaming media.
        /// </summary>
        internal int HighPriorityCount { get; set; } = 15;

        internal int LowPriorityCount => HighPriorityCount * 2;

        public void Initialise (ITorrentData torrentData, IReadOnlyList<BitField> ignoringBitfields)
        {
            TorrentData = torrentData;

            var standardPicker = new StandardPicker ();

            HighPriorityPicker = IgnoringPicker.Wrap (new PriorityPicker (standardPicker), ignoringBitfields);

            LowPriorityPicker = new RandomisedPicker (standardPicker);
            LowPriorityPicker = new RarestFirstPicker (LowPriorityPicker);
            LowPriorityPicker = new PriorityPicker (LowPriorityPicker);
            LowPriorityPicker = IgnoringPicker.Wrap (LowPriorityPicker, ignoringBitfields);

            LowPriorityPicker.Initialise (torrentData);
            HighPriorityPicker.Initialise (torrentData);
        }

        public void AddRequests (IReadOnlyList<IPeerWithMessaging> peers, BitField bitfield)
        {
            if (!RefreshAfterSeeking)
                return;

            RefreshAfterSeeking = true;
            var allPeers = peers
                .OrderBy (t => -t.DownloadSpeed)
                .ToArray ();

            // It's only worth cancelling requests for peers which support cancellation. This is part of
            // of the fast peer extensions. For peers which do not support cancellation all we can do is
            // close the connection or allow the existing request queue to drain naturally.
            var start = HighPriorityPieceIndex;
            var end = Math.Min (bitfield.Length - 1, start + HighPriorityCount - 1);
            if (bitfield.FirstFalse (start, end) != -1) {
                foreach (var peer in allPeers.Where (p => p.CanCancelRequests)) {
                    if (HighPriorityPieceIndex > 0)
                        peer.EnqueueCancellations (Picker.CancelRequests (peer, 0, HighPriorityPieceIndex - 1));

                    if (HighPriorityPieceIndex + HighPriorityCount < bitfield.Length)
                        peer.EnqueueCancellations (Picker.CancelRequests (peer, HighPriorityPieceIndex + HighPriorityCount, bitfield.Length - 1));
                }
            }

            var fastestPeers = allPeers
                .Where (t => t.DownloadSpeed > 50 * 1024)
                .ToArray ();

            // Queue up 12 pieces for each of our fastest peers. At a download
            // speed of 50kB/sec this should be 3 seconds of transfer for each peer.
            // We queue from peers which support cancellation first as their queue
            // is likely to be empty.
            foreach (var supportsFastPeer in new[] { true, false }) {
                for (int i = 0; i < 4; i++) {
                    foreach (var peer in fastestPeers.Where (p => p.SupportsFastPeer == supportsFastPeer)) {
                        AddRequests (peer, peers, bitfield, 0, bitfield.Length - 1, 2, 6);
                    }
                }
            }

            // Then fill up the request queues for all peers
            foreach (var peer in peers)
                AddRequests (peer, peers, bitfield);
        }

        public void AddRequests (IPeerWithMessaging peer, IReadOnlyList<IPeerWithMessaging> allPeers, BitField bitfield)
        {
            // The first two pieces in the high priority set should be requested multiple times to ensure fast delivery
            for (int i = HighPriorityPieceIndex; i < bitfield.Length && i <= HighPriorityPieceIndex + 1; i++)
                AddRequests (peer, allPeers, bitfield, HighPriorityPieceIndex, HighPriorityPieceIndex, 2);

            var lowPriorityEnd = Math.Min (bitfield.Length - 1, HighPriorityPieceIndex + LowPriorityCount - 1);
            AddRequests (peer, allPeers, bitfield, HighPriorityPieceIndex, lowPriorityEnd, 1);
        }

        void AddRequests (IPeerWithMessaging peer, IReadOnlyList<IPeerWithMessaging> allPeers, BitField bitfield, int startPieceIndex, int endPieceIndex, int maxDuplicates, int? preferredMaxRequests = null)
        {
            if (!peer.CanRequestMorePieces)
                return;

            int preferredRequestAmount = peer.PreferredRequestAmount (TorrentData.PieceLength);
            var maxRequests = Math.Min (preferredMaxRequests ?? 3, peer.MaxPendingRequests);

            if (peer.AmRequestingPiecesCount >= maxRequests)
                return;

            // FIXME: Add a test to ensure we do not unintentionally request blocks off peers which are choking us.
            // This used to say if (!peer.IsChoing || peer.SupportsFastPeer), and with the recent changes we might
            // not actually guarantee that 'ContinueExistingRequest' or 'ContinueAnyExistingRequest' properly takes
            // into account that a peer which is choking us can *only* resume a 'fast piece' in the 'AmAllowedfastPiece' list.
            if (!peer.IsChoking) {
                while (peer.AmRequestingPiecesCount < maxRequests) {
                    BlockInfo? request = Picker.ContinueAnyExistingRequest (peer, startPieceIndex, endPieceIndex, maxDuplicates);
                    if (request != null)
                        peer.EnqueueRequest (request.Value);
                    else
                        break;
                }
            }

            // If the peer supports fast peer and they are choking us, they'll still send pieces in the allowed fast set.
            if (peer.SupportsFastPeer && peer.IsChoking) {
                while (peer.AmRequestingPiecesCount < maxRequests) {
                    BlockInfo? request = Picker.ContinueExistingRequest (peer, startPieceIndex, endPieceIndex);
                    if (request != null)
                        peer.EnqueueRequest (request.Value);
                    else
                        break;
                }
            }

            // Should/could we simplify things for IPiecePicker implementations by guaranteeing IPiecePicker.PickPiece calls will
            // only be made to pieces which *can* be requested? Why not!
            // FIXME add a test for this.
            if (!peer.IsChoking || (peer.SupportsFastPeer && peer.IsAllowedFastPieces.Count > 0)) {
                while (peer.AmRequestingPiecesCount < maxRequests) {
                    IList<BlockInfo> request = PriorityPick (peer, peer.BitField, allPeers, preferredRequestAmount, 0, TorrentData.PieceCount () - 1);
                    if (request != null && request.Count > 0)
                        peer.EnqueueRequests (request);
                    else
                        break;
                }
            }

            if (!peer.IsChoking && peer.AmRequestingPiecesCount == 0) {
                while (peer.AmRequestingPiecesCount < maxRequests) {
                    BlockInfo? request = Picker.ContinueAnyExistingRequest (peer, HighPriorityPieceIndex, bitfield.Length - 1, 1);
                    // If this peer is a seeder and we are unable to request any new blocks, then we should enter
                    // endgame mode. Every block has been requested at least once at this point.
                    if (request == null && (InEndgameMode || peer.IsSeeder)) {
                        request = Picker.ContinueAnyExistingRequest (peer, 0, TorrentData.PieceCount () - 1, 2);
                        // FIXME: What if the picker is choosing to not allocate pieces? Then it's not endgame mode.
                        // This should be deterministic, not a heuristic?
                        InEndgameMode |= request != null && (bitfield.Length - bitfield.TrueCount) < 10;
                    }

                    if (request != null)
                        peer.EnqueueRequest (request.Value);
                    else
                        break;
                }
            }
        }

         IList<BlockInfo> PriorityPick (IPeer peer, BitField available, IReadOnlyList<IPeer> otherPeers, int count, int startIndex, int endIndex)
        {
            BlockInfo? request;
            IList<BlockInfo> bundle;

            if (HighPriorityPieceIndex >= startIndex && HighPriorityPieceIndex <= endIndex) {
                var start = HighPriorityPieceIndex;
                var end = Math.Min (endIndex, HighPriorityPieceIndex + HighPriorityCount - 1);

                for (int prioritised = start; prioritised <= start + 1 && prioritised <= end; prioritised++) {
                    if (available[prioritised]) {
                        if ((bundle = HighPriorityPicker.PickPiece (peer, available, otherPeers, count, prioritised, prioritised)) != null)
                            return bundle;
                        if ((request = HighPriorityPicker.ContinueAnyExistingRequest (peer, prioritised, prioritised, 3)) != null)
                            return new[] { request.Value };
                    }
                }

                if ((bundle = HighPriorityPicker.PickPiece (peer, available, otherPeers, count, start, end)) != null)
                    return bundle;
            }

            if (endIndex < HighPriorityPieceIndex)
                return null;

            var lowPriorityEndIndex = Math.Min (HighPriorityPieceIndex + LowPriorityCount, endIndex);
            if ((bundle = LowPriorityPicker.PickPiece (peer, available, otherPeers, count, HighPriorityPieceIndex, lowPriorityEndIndex)) != null)
                return bundle;

            // If we're downloading from the 'not important at all' section, queue up at most 2.
            if (peer.AmRequestingPiecesCount > 2)
                return null;

            return LowPriorityPicker.PickPiece (peer, available, otherPeers, count, HighPriorityPieceIndex, endIndex);
        }


        /// <summary>
        /// Cancel any pending requests and then issue new requests so we immediately download pieces from the new high
        /// priority set.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="position"></param>
        public void SeekToPosition (ITorrentFileInfo file, long position)
        {
            // Update the high priority set, then cancel pending requests.
            var oldIndex = HighPriorityPieceIndex;
            ReadToPosition (file, position);
            RefreshAfterSeeking |= oldIndex != HighPriorityPieceIndex;
        }

        /// <summary>
        /// Inform the picker that we have sequentially read data and so will need to update the high priority set without
        /// cancelling pending requests.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="position"></param>
        public void ReadToPosition (ITorrentFileInfo file, long position)
        {
            HighPriorityPieceIndex = Math.Min (file.EndPieceIndex, TorrentData.ByteOffsetToPieceIndex (position + file.OffsetInTorrent));
        }
    }
}
