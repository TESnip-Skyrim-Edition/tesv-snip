// DeflaterEngine.cs
//
// Copyright (C) 2001 Mike Krueger
// Copyright (C) 2004 John Reilly
//
// This file was translated from java, it was part of the GNU Classpath
// Copyright (C) 2001 Free Software Foundation, Inc.
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
//
// Linking this library statically or dynamically with other modules is
// making a combined work based on this library.  Thus, the terms and
// conditions of the GNU General Public License cover the whole
// combination.
// 
// As a special exception, the copyright holders of this library give you
// permission to link this library with independent modules to produce an
// executable, regardless of the license terms of these independent
// modules, and to copy and distribute the resulting executable under
// terms of your choice, provided that you also meet, for each linked
// independent module, the terms and conditions of the license of that
// module.  An independent module is a module which is not derived from
// or based on this library.  If you modify this library, you may extend
// this exception to your version of the library, but you are not
// obligated to do so.  If you do not wish to do so, delete this
// exception statement from your version.

using System;
using ICSharpCode.SharpZipLib.Checksums;

namespace ICSharpCode.SharpZipLib.Zip.Compression
{
    /// <summary>
    /// Strategies for deflater
    /// </summary>
    internal enum DeflateStrategy
    {
        /// <summary>
        /// The default strategy
        /// </summary>
        Default = 0,

        /// <summary>
        /// This strategy will only allow longer string repetitions.  It is
        /// useful for random data with a small character set.
        /// </summary>
        Filtered = 1,


        /// <summary>
        /// This strategy will not look for string repetitions at all.  It
        /// only encodes with Huffman trees (which means, that more common
        /// characters get a smaller encoding.
        /// </summary>
        HuffmanOnly = 2
    }

    // DEFLATE ALGORITHM:
    // 
    // The uncompressed stream is inserted into the window array.  When
    // the window array is full the first half is thrown away and the
    // second half is copied to the beginning.
    //
    // The head array is a hash table.  Three characters build a hash value
    // and they the value points to the corresponding index in window of 
    // the last string with this hash.  The prev array implements a
    // linked list of matches with the same hash: prev[index & WMASK] points
    // to the previous index with the same hash.
    // 


    /// <summary>
    /// Low level compression engine for deflate algorithm which uses a 32K sliding window
    /// with secondary compression from Huffman/Shannon-Fano codes.
    /// </summary>
    internal class DeflaterEngine
    {
        #region Constants

        private const int TooFar = 4096;

        #endregion

        #region Constructors

        /// <summary>
        /// Construct instance with pending buffer
        /// </summary>
        /// <param name="pending">
        /// Pending buffer to use
        /// </param>>
        public DeflaterEngine(DeflaterPending pending)
        {
            this.pending = pending;
            huffman = new DeflaterHuffman(pending);
            adler = new Adler32();

            window = new byte[2*DeflaterConstants.WSIZE];
            head = new short[DeflaterConstants.HASH_SIZE];
            prev = new short[DeflaterConstants.WSIZE];

            // We start at index 1, to avoid an implementation deficiency, that
            // we cannot build a repeat pattern at index 0.
            blockStart = strstart = 1;
        }

        #endregion

        /// <summary>
        /// Deflate drives actual compression of data
        /// </summary>
        /// <param name="flush">True to flush input buffers</param>
        /// <param name="finish">Finish deflation with the current input.</param>
        /// <returns>Returns true if progress has been made.</returns>
        public bool Deflate(bool flush, bool finish)
        {
            bool progress;
            do
            {
                FillWindow();
                bool canFlush = flush && (inputOff == inputEnd);

#if DebugDeflation
				if (DeflaterConstants.DEBUGGING) {
					Console.WriteLine("window: [" + blockStart + "," + strstart + ","
								+ lookahead + "], " + compressionFunction + "," + canFlush);
				}
#endif
                switch (compressionFunction)
                {
                    case DeflaterConstants.DEFLATE_STORED:
                        progress = DeflateStored(canFlush, finish);
                        break;
                    case DeflaterConstants.DEFLATE_FAST:
                        progress = DeflateFast(canFlush, finish);
                        break;
                    case DeflaterConstants.DEFLATE_SLOW:
                        progress = DeflateSlow(canFlush, finish);
                        break;
                    default:
                        throw new InvalidOperationException("unknown compressionFunction");
                }
            } while (pending.IsFlushed && progress); // repeat while we have no pending output and progress was made
            return progress;
        }

        /// <summary>
        /// Sets input data to be deflated.  Should only be called when <code>NeedsInput()</code>
        /// returns true
        /// </summary>
        /// <param name="buffer">The buffer containing input data.</param>
        /// <param name="offset">The offset of the first byte of data.</param>
        /// <param name="count">The number of bytes of data to use as input.</param>
        public void SetInput(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException("offset");
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count");
            }

            if (inputOff < inputEnd)
            {
                throw new InvalidOperationException("Old input was not completely processed");
            }

            int end = offset + count;

            /* We want to throw an ArrayIndexOutOfBoundsException early.  The
			* check is very tricky: it also handles integer wrap around.
			*/
            if ((offset > end) || (end > buffer.Length))
            {
                throw new ArgumentOutOfRangeException("count");
            }

            inputBuf = buffer;
            inputOff = offset;
            inputEnd = end;
        }

        /// <summary>
        /// Determines if more <see cref="SetInput">input</see> is needed.
        /// </summary>		
        /// <returns>Return true if input is needed via <see cref="SetInput">SetInput</see></returns>
        public bool NeedsInput()
        {
            return (inputEnd == inputOff);
        }

        /// <summary>
        /// Reset internal state
        /// </summary>		
        public void Reset()
        {
            huffman.Reset();
            adler.Reset();
            blockStart = strstart = 1;
            lookahead = 0;
            totalIn = 0;
            prevAvailable = false;
            matchLen = DeflaterConstants.MIN_MATCH - 1;

            for (int i = 0; i < DeflaterConstants.HASH_SIZE; i++)
            {
                head[i] = 0;
            }

            for (int i = 0; i < DeflaterConstants.WSIZE; i++)
            {
                prev[i] = 0;
            }
        }

        /// <summary>
        /// Reset Adler checksum
        /// </summary>		
        public void ResetAdler()
        {
            adler.Reset();
        }

        /// <summary>
        /// Get current value of Adler checksum
        /// </summary>		
        public int Adler
        {
            get { return unchecked((int) adler.Value); }
        }

        /// <summary>
        /// Get/set the <see cref="DeflateStrategy">deflate strategy</see>
        /// </summary>		
        public DeflateStrategy Strategy
        {
            set { strategy = value; }
        }

        /// <summary>
        /// Set the deflate level (0-9)
        /// </summary>
        /// <param name="level">The value to set the level to.</param>
        public void SetLevel(int level)
        {
            if ((level < 0) || (level > 9))
            {
                throw new ArgumentOutOfRangeException("level");
            }

            goodLength = DeflaterConstants.GOOD_LENGTH[level];
            max_lazy = DeflaterConstants.MAX_LAZY[level];
            niceLength = DeflaterConstants.NICE_LENGTH[level];
            max_chain = DeflaterConstants.MAX_CHAIN[level];

            if (DeflaterConstants.COMPR_FUNC[level] != compressionFunction)
            {
                switch (compressionFunction)
                {
                    case DeflaterConstants.DEFLATE_STORED:
                        if (strstart > blockStart)
                        {
                            huffman.FlushStoredBlock(window, blockStart,
                                                     strstart - blockStart, false);
                            blockStart = strstart;
                        }
                        UpdateHash();
                        break;

                    case DeflaterConstants.DEFLATE_FAST:
                        if (strstart > blockStart)
                        {
                            huffman.FlushBlock(window, blockStart, strstart - blockStart,
                                               false);
                            blockStart = strstart;
                        }
                        break;

                    case DeflaterConstants.DEFLATE_SLOW:
                        if (prevAvailable)
                        {
                            huffman.TallyLit(window[strstart - 1] & 0xff);
                        }
                        if (strstart > blockStart)
                        {
                            huffman.FlushBlock(window, blockStart, strstart - blockStart, false);
                            blockStart = strstart;
                        }
                        prevAvailable = false;
                        matchLen = DeflaterConstants.MIN_MATCH - 1;
                        break;
                }
                compressionFunction = DeflaterConstants.COMPR_FUNC[level];
            }
        }

        /// <summary>
        /// Fill the window
        /// </summary>
        public void FillWindow()
        {
            /* If the window is almost full and there is insufficient lookahead,
			 * move the upper half to the lower one to make room in the upper half.
			 */
            if (strstart >= DeflaterConstants.WSIZE + DeflaterConstants.MAX_DIST)
            {
                SlideWindow();
            }

            /* If there is not enough lookahead, but still some input left,
			 * read in the input
			 */
            while (lookahead < DeflaterConstants.MIN_LOOKAHEAD && inputOff < inputEnd)
            {
                int more = 2*DeflaterConstants.WSIZE - lookahead - strstart;

                if (more > inputEnd - inputOff)
                {
                    more = inputEnd - inputOff;
                }

                Array.Copy(inputBuf, inputOff, window, strstart + lookahead, more);
                adler.Update(inputBuf, inputOff, more);

                inputOff += more;
                totalIn += more;
                lookahead += more;
            }

            if (lookahead >= DeflaterConstants.MIN_MATCH)
            {
                UpdateHash();
            }
        }

        private void UpdateHash()
        {
/*
			if (DEBUGGING) {
				Console.WriteLine("updateHash: "+strstart);
			}
*/
            ins_h = (window[strstart] << DeflaterConstants.HASH_SHIFT) ^ window[strstart + 1];
        }

        /// <summary>
        /// Inserts the current string in the head hash and returns the previous
        /// value for this hash.
        /// </summary>
        /// <returns>The previous hash value</returns>
        private int InsertString()
        {
            short match;
            int hash = ((ins_h << DeflaterConstants.HASH_SHIFT) ^ window[strstart + (DeflaterConstants.MIN_MATCH - 1)]) &
                       DeflaterConstants.HASH_MASK;

#if DebugDeflation
			if (DeflaterConstants.DEBUGGING) 
			{
				if (hash != (((window[strstart] << (2*HASH_SHIFT)) ^ 
								  (window[strstart + 1] << HASH_SHIFT) ^ 
								  (window[strstart + 2])) & HASH_MASK)) {
						throw new SharpZipBaseException("hash inconsistent: " + hash + "/"
												+window[strstart] + ","
												+window[strstart + 1] + ","
												+window[strstart + 2] + "," + HASH_SHIFT);
					}
			}
#endif
            prev[strstart & DeflaterConstants.WMASK] = match = head[hash];
            head[hash] = unchecked((short) strstart);
            ins_h = hash;
            return match & 0xffff;
        }

        private void SlideWindow()
        {
            Array.Copy(window, DeflaterConstants.WSIZE, window, 0, DeflaterConstants.WSIZE);
            matchStart -= DeflaterConstants.WSIZE;
            strstart -= DeflaterConstants.WSIZE;
            blockStart -= DeflaterConstants.WSIZE;

            // Slide the hash table (could be avoided with 32 bit values
            // at the expense of memory usage).
            for (int i = 0; i < DeflaterConstants.HASH_SIZE; ++i)
            {
                int m = head[i] & 0xffff;
                head[i] = (short) (m >= DeflaterConstants.WSIZE ? (m - DeflaterConstants.WSIZE) : 0);
            }

            // Slide the prev table.
            for (int i = 0; i < DeflaterConstants.WSIZE; i++)
            {
                int m = prev[i] & 0xffff;
                prev[i] = (short) (m >= DeflaterConstants.WSIZE ? (m - DeflaterConstants.WSIZE) : 0);
            }
        }

        /// <summary>
        /// Find the best (longest) string in the window matching the 
        /// string starting at strstart.
        ///
        /// Preconditions:
        /// <code>
        /// strstart + MAX_MATCH &lt;= window.length.</code>
        /// </summary>
        /// <param name="curMatch"></param>
        /// <returns>True if a match greater than the minimum length is found</returns>
        private bool FindLongestMatch(int curMatch)
        {
            int chainLength = max_chain;
            int niceLength = this.niceLength;
            short[] prev = this.prev;
            int scan = strstart;
            int match;
            int best_end = strstart + matchLen;
            int best_len = Math.Max(matchLen, DeflaterConstants.MIN_MATCH - 1);

            int limit = Math.Max(strstart - DeflaterConstants.MAX_DIST, 0);

            int strend = strstart + DeflaterConstants.MAX_MATCH - 1;
            byte scan_end1 = window[best_end - 1];
            byte scan_end = window[best_end];

            // Do not waste too much time if we already have a good match:
            if (best_len >= goodLength)
            {
                chainLength >>= 2;
            }

            /* Do not look for matches beyond the end of the input. This is necessary
			* to make deflate deterministic.
			*/
            if (niceLength > lookahead)
            {
                niceLength = lookahead;
            }

            do
            {
                if (window[curMatch + best_len] != scan_end ||
                    window[curMatch + best_len - 1] != scan_end1 ||
                    window[curMatch] != window[scan] ||
                    window[curMatch + 1] != window[scan + 1])
                {
                    continue;
                }

                match = curMatch + 2;
                scan += 2;

                /* We check for insufficient lookahead only every 8th comparison;
				* the 256th check will be made at strstart + 258.
				*/
                while (
                    window[++scan] == window[++match] &&
                    window[++scan] == window[++match] &&
                    window[++scan] == window[++match] &&
                    window[++scan] == window[++match] &&
                    window[++scan] == window[++match] &&
                    window[++scan] == window[++match] &&
                    window[++scan] == window[++match] &&
                    window[++scan] == window[++match] &&
                    (scan < strend))
                {
                    // Do nothing
                }

                if (scan > best_end)
                {
                    matchStart = curMatch;
                    best_end = scan;
                    best_len = scan - strstart;

                    if (best_len >= niceLength)
                    {
                        break;
                    }

                    scan_end1 = window[best_end - 1];
                    scan_end = window[best_end];
                }
                scan = strstart;
            } while ((curMatch = (prev[curMatch & DeflaterConstants.WMASK] & 0xffff)) > limit && --chainLength != 0);

            matchLen = Math.Min(best_len, lookahead);
            return matchLen >= DeflaterConstants.MIN_MATCH;
        }

        private bool DeflateStored(bool flush, bool finish)
        {
            if (!flush && (lookahead == 0))
            {
                return false;
            }

            strstart += lookahead;
            lookahead = 0;

            int storedLength = strstart - blockStart;

            if ((storedLength >= DeflaterConstants.MAX_BLOCK_SIZE) || // Block is full
                (blockStart < DeflaterConstants.WSIZE && storedLength >= DeflaterConstants.MAX_DIST) ||
                // Block may move out of window
                flush)
            {
                bool lastBlock = finish;
                if (storedLength > DeflaterConstants.MAX_BLOCK_SIZE)
                {
                    storedLength = DeflaterConstants.MAX_BLOCK_SIZE;
                    lastBlock = false;
                }

                huffman.FlushStoredBlock(window, blockStart, storedLength, lastBlock);
                blockStart += storedLength;
                return !lastBlock;
            }
            return true;
        }

        private bool DeflateFast(bool flush, bool finish)
        {
            if (lookahead < DeflaterConstants.MIN_LOOKAHEAD && !flush)
            {
                return false;
            }

            while (lookahead >= DeflaterConstants.MIN_LOOKAHEAD || flush)
            {
                if (lookahead == 0)
                {
                    // We are flushing everything
                    huffman.FlushBlock(window, blockStart, strstart - blockStart, finish);
                    blockStart = strstart;
                    return false;
                }

                if (strstart > 2*DeflaterConstants.WSIZE - DeflaterConstants.MIN_LOOKAHEAD)
                {
                    /* slide window, as FindLongestMatch needs this.
					 * This should only happen when flushing and the window
					 * is almost full.
					 */
                    SlideWindow();
                }

                int hashHead;
                if (lookahead >= DeflaterConstants.MIN_MATCH &&
                    (hashHead = InsertString()) != 0 &&
                    strategy != DeflateStrategy.HuffmanOnly &&
                    strstart - hashHead <= DeflaterConstants.MAX_DIST &&
                    FindLongestMatch(hashHead))
                {
                    // longestMatch sets matchStart and matchLen

                    bool full = huffman.TallyDist(strstart - matchStart, matchLen);

                    lookahead -= matchLen;
                    if (matchLen <= max_lazy && lookahead >= DeflaterConstants.MIN_MATCH)
                    {
                        while (--matchLen > 0)
                        {
                            ++strstart;
                            InsertString();
                        }
                        ++strstart;
                    }
                    else
                    {
                        strstart += matchLen;
                        if (lookahead >= DeflaterConstants.MIN_MATCH - 1)
                        {
                            UpdateHash();
                        }
                    }
                    matchLen = DeflaterConstants.MIN_MATCH - 1;
                    if (!full)
                    {
                        continue;
                    }
                }
                else
                {
                    // No match found
                    huffman.TallyLit(window[strstart] & 0xff);
                    ++strstart;
                    --lookahead;
                }

                if (huffman.IsFull())
                {
                    bool lastBlock = finish && (lookahead == 0);
                    huffman.FlushBlock(window, blockStart, strstart - blockStart, lastBlock);
                    blockStart = strstart;
                    return !lastBlock;
                }
            }
            return true;
        }

        private bool DeflateSlow(bool flush, bool finish)
        {
            if (lookahead < DeflaterConstants.MIN_LOOKAHEAD && !flush)
            {
                return false;
            }

            while (lookahead >= DeflaterConstants.MIN_LOOKAHEAD || flush)
            {
                if (lookahead == 0)
                {
                    if (prevAvailable)
                    {
                        huffman.TallyLit(window[strstart - 1] & 0xff);
                    }
                    prevAvailable = false;

                    // We are flushing everything
#if DebugDeflation
					if (DeflaterConstants.DEBUGGING && !flush) 
					{
						throw new SharpZipBaseException("Not flushing, but no lookahead");
					}
#endif
                    huffman.FlushBlock(window, blockStart, strstart - blockStart,
                                       finish);
                    blockStart = strstart;
                    return false;
                }

                if (strstart >= 2*DeflaterConstants.WSIZE - DeflaterConstants.MIN_LOOKAHEAD)
                {
                    /* slide window, as FindLongestMatch needs this.
					 * This should only happen when flushing and the window
					 * is almost full.
					 */
                    SlideWindow();
                }

                int prevMatch = matchStart;
                int prevLen = matchLen;
                if (lookahead >= DeflaterConstants.MIN_MATCH)
                {
                    int hashHead = InsertString();

                    if (strategy != DeflateStrategy.HuffmanOnly &&
                        hashHead != 0 &&
                        strstart - hashHead <= DeflaterConstants.MAX_DIST &&
                        FindLongestMatch(hashHead))
                    {
                        // longestMatch sets matchStart and matchLen

                        // Discard match if too small and too far away
                        if (matchLen <= 5 &&
                            (strategy == DeflateStrategy.Filtered ||
                             (matchLen == DeflaterConstants.MIN_MATCH && strstart - matchStart > TooFar)))
                        {
                            matchLen = DeflaterConstants.MIN_MATCH - 1;
                        }
                    }
                }

                // previous match was better
                if ((prevLen >= DeflaterConstants.MIN_MATCH) && (matchLen <= prevLen))
                {
                    huffman.TallyDist(strstart - 1 - prevMatch, prevLen);
                    prevLen -= 2;
                    do
                    {
                        strstart++;
                        lookahead--;
                        if (lookahead >= DeflaterConstants.MIN_MATCH)
                        {
                            InsertString();
                        }
                    } while (--prevLen > 0);

                    strstart ++;
                    lookahead--;
                    prevAvailable = false;
                    matchLen = DeflaterConstants.MIN_MATCH - 1;
                }
                else
                {
                    if (prevAvailable)
                    {
                        huffman.TallyLit(window[strstart - 1] & 0xff);
                    }
                    prevAvailable = true;
                    strstart++;
                    lookahead--;
                }

                if (huffman.IsFull())
                {
                    int len = strstart - blockStart;
                    if (prevAvailable)
                    {
                        len--;
                    }
                    bool lastBlock = (finish && (lookahead == 0) && !prevAvailable);
                    huffman.FlushBlock(window, blockStart, len, lastBlock);
                    blockStart += len;
                    return !lastBlock;
                }
            }
            return true;
        }

        #region Instance Fields

        // Hash index of string to be inserted
        private int ins_h;

        /// <summary>
        /// Hashtable, hashing three characters to an index for window, so
        /// that window[index]..window[index+2] have this hash code.  
        /// Note that the array should really be unsigned short, so you need
        /// to and the values with 0xffff.
        /// </summary>
        private readonly short[] head;

        /// <summary>
        /// <code>prev[index &amp; WMASK]</code> points to the previous index that has the
        /// same hash code as the string starting at index.  This way 
        /// entries with the same hash code are in a linked list.
        /// Note that the array should really be unsigned short, so you need
        /// to and the values with 0xffff.
        /// </summary>
        private readonly short[] prev;

        private int matchStart;
        // Length of best match
        private int matchLen;
        // Set if previous match exists
        private bool prevAvailable;
        private int blockStart;

        /// <summary>
        /// Points to the current character in the window.
        /// </summary>
        private int strstart;

        /// <summary>
        /// lookahead is the number of characters starting at strstart in
        /// window that are valid.
        /// So window[strstart] until window[strstart+lookahead-1] are valid
        /// characters.
        /// </summary>
        private int lookahead;

        /// <summary>
        /// This array contains the part of the uncompressed stream that 
        /// is of relevance.  The current character is indexed by strstart.
        /// </summary>
        private readonly byte[] window;

        private DeflateStrategy strategy;
        private int max_chain, max_lazy, niceLength, goodLength;

        /// <summary>
        /// The current compression function.
        /// </summary>
        private int compressionFunction;

        /// <summary>
        /// The input data for compression.
        /// </summary>
        private byte[] inputBuf;

        /// <summary>
        /// The total bytes of input read.
        /// </summary>
        private long totalIn;

        /// <summary>
        /// The offset into inputBuf, where input data starts.
        /// </summary>
        private int inputOff;

        /// <summary>
        /// The end offset of the input data.
        /// </summary>
        private int inputEnd;

        private readonly DeflaterPending pending;
        private readonly DeflaterHuffman huffman;

        /// <summary>
        /// The adler checksum
        /// </summary>
        private readonly Adler32 adler;

        #endregion
    }
}