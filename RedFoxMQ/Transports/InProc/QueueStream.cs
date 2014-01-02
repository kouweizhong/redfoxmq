﻿// 
// Copyright 2013 Hans Wolff
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// 

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RedFoxMQ.Transports.InProc
{
    class QueueStream : Stream
    {
        readonly BlockingCollection<byte[]> _buffers = new BlockingCollection<byte[]>();
        byte[] _currentBuffer;
        int _currentBufferOffset;
        readonly InterlockedBoolean _isBusyReading = new InterlockedBoolean();

        private readonly bool _blocking;
        private int _length;

        public override long Length
        {
            get { return _length; }
        }

        public QueueStream() : this(false)
        {
        }

        public QueueStream(bool blocking)
        {
            _blocking = blocking;

            _closeCancellationTokenSource = new CancellationTokenSource();
            _closeCancellationToken = _closeCancellationTokenSource.Token;
        }

        private readonly CancellationTokenSource _closeCancellationTokenSource;
        private readonly CancellationToken _closeCancellationToken;
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _closeCancellationTokenSource.Cancel(false);
        }

        public byte[] ReadAll()
        {
            if (_isBusyReading.Set(true))
                throw new InvalidOperationException("Access from different threads, QueueStream is not thread-safe");

            try
            {
                var bytesAlreadyRead = 0;

                var localBuffers = new Queue<byte[]>();
                byte[] localBuffer;
                byte[] firstBuffer = null;
                int localBuffersSize = 0;
                while (_buffers.TryTake(out localBuffer))
                {
                    if (firstBuffer == null) firstBuffer = localBuffer;
                    localBuffersSize += localBuffer.Length;
                    localBuffers.Enqueue(localBuffer);
                }

                if (firstBuffer != null &&
                    localBuffersSize == firstBuffer.Length &&
                    _currentBuffer == null)
                {
                    // in many scenarios this is sufficient and prevents unnecessary 
                    // buffer creation and memory copy operations
                    return firstBuffer;
                }

                var bufferSize = _currentBuffer != null ? _currentBuffer.Length - _currentBufferOffset : 0;
                bufferSize += localBuffersSize;

                var buffer = new byte[bufferSize];

                do
                {
                    if (_currentBuffer == null || _currentBufferOffset == _currentBuffer.Length)
                    {
                        if (localBuffers.Count == 0)
                            return buffer;
                        _currentBuffer = localBuffers.Dequeue();
                        _currentBufferOffset = 0;
                    }

                    var bytesAvailable = _currentBuffer.Length - _currentBufferOffset;
                    var bytesToReadFromCurrentBuffer = bytesAvailable;

                    Array.Copy(_currentBuffer, _currentBufferOffset, buffer, bytesAlreadyRead,
                        bytesToReadFromCurrentBuffer);

                    _currentBufferOffset += bytesToReadFromCurrentBuffer;
                    bytesAlreadyRead += bytesToReadFromCurrentBuffer;
                    Interlocked.Add(ref _length, -bytesToReadFromCurrentBuffer);

                } while (true);
            }
            finally
            {
                _isBusyReading.Set(false);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0) throw new ArgumentOutOfRangeException("offset", String.Format("Offset cannot be negative (but was: {0})", offset));
            if (count == 0) return 0;
            if (count < 0) throw new ArgumentOutOfRangeException("count", String.Format("Cannot read a negative number of bytes (parameter 'count' is: {0})", count));

            if (_isBusyReading.Set(true))
                throw new InvalidOperationException("Access from different threads, QueueStream is not thread-safe");

            try
            {
                    var bytesAlreadyRead = 0;
                    do
                    {
                        if (_currentBuffer == null || _currentBufferOffset == _currentBuffer.Length)
                        {
                            if (_blocking)
                            {
                                try
                                {
                                    _currentBuffer = _buffers.Take(_closeCancellationToken);
                                }
                                catch (OperationCanceledException)
                                {
                                    if (_closeCancellationTokenSource.IsCancellationRequested)
                                        throw new ObjectDisposedException(typeof (QueueStream).Name);
                                    throw;
                                }
                            }
                            else
                            {
                                if (!_buffers.TryTake(out _currentBuffer))
                                    return bytesAlreadyRead;
                            }
                            _currentBufferOffset = 0;
                        }

                        var bytesToRead = count - bytesAlreadyRead;
                        var bytesAvailable = _currentBuffer.Length - _currentBufferOffset;

                        var bytesToReadFromCurrentBuffer = Math.Min(bytesToRead, bytesAvailable);
                        Array.Copy(_currentBuffer, _currentBufferOffset, buffer, offset + bytesAlreadyRead,
                            bytesToReadFromCurrentBuffer);

                        _currentBufferOffset += bytesToReadFromCurrentBuffer;
                        bytesAlreadyRead += bytesToReadFromCurrentBuffer;
                        Interlocked.Add(ref _length, -bytesToRead);

                    } while (bytesAlreadyRead < count);

                    return bytesAlreadyRead;
            }
            finally
            {
                _isBusyReading.Set(false);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0) throw new ArgumentOutOfRangeException("offset", String.Format("Offset cannot be negative (but was: {0})", offset));
            if (count == 0) return;
            if (count < 0) throw new ArgumentOutOfRangeException("count", String.Format("Cannot write a negative number of bytes (parameter 'count' is: {0})", count));
            if (_closeCancellationTokenSource.IsCancellationRequested)
                throw new ObjectDisposedException(typeof(QueueStream).Name);

            var splice = new byte[count];
            Array.Copy(buffer, offset, splice, 0, count);
            _buffers.Add(splice);
            Interlocked.Add(ref _length, count);
        }

        public override void Close()
        {
            _closeCancellationTokenSource.Cancel();
            base.Close();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException("Stream is not seekable");
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("Cannot set stream length");
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

        public override long Position
        {
            get { return 0; }
            set { throw new NotSupportedException("Stream is not seekable"); }
        }
    }
}
