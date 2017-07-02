﻿//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    public class MessageBusWriteStream : IMessageBusWriteStream
    {
        private IProducer _producer;
        private readonly long _packetSize;
        private readonly string _endPoint;
        private readonly string _sequenceId;
        private readonly IConfiguration _configuration;
        private Int64 _packetsSent;

        public MessageBusWriteStream(IProducer producer, string endPoint, string sequenceId, IConfiguration configuration)
        {
            _producer = producer;
            _endPoint = endPoint;
            _sequenceId = sequenceId;
            _configuration = configuration;
            _packetSize = producer.MaximumMessageSize;
            _packetsSent = 0;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            var currentPacketSize = (count <= (int)_packetSize) ? count : (int)_packetSize;

            for (int i = offset; i < count; i += currentPacketSize)
            {
                var subArray = SubArray(buffer, i, currentPacketSize);

                _packetsSent++;
                _producer.SendBytes(_endPoint, subArray, new Dictionary<string, string>
                {
                    { "SequenceId", _sequenceId },
                    { "PacketNumber", _packetsSent.ToString() }
                });
            }
        }

        private static byte[] SubArray(byte[] data, int index, int length)
        {
            if (data.Length < index + length)
            {
                length = data.Length - index;
            }
            byte[] result = new byte[length];
            Array.Copy(data, index, result, 0, length);
            return result;
        }

        public void Close()
        {
            _packetsSent++;
            _producer.SendBytes(_endPoint, new byte[0], new Dictionary<string, string>
            {
                { "SequenceId", _sequenceId },
                { "Stop", string.Empty },
                { "PacketNumber", _packetsSent.ToString()}
            });
        }
        
        public void Dispose()
        {
            Close();
            _producer = null;
        }
    }
}