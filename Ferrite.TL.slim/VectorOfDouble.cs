// 
// Project Ferrite is an Implementation of the Telegram Server API
// Copyright 2022 Aykut Alparslan KOC <aykutalparslan@msn.com>
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
// 

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ferrite.TL.slim;

public ref struct VectorOfDouble
{
    private Span<double> _buff;
    private int _offset;
    public VectorOfDouble()
    {
        _buff = new double[32];
        SetConstructor(unchecked((int)0x1cb5c415));
        SetCount(0);
        _offset = 1;
    }
    public VectorOfDouble(Span<byte> buffer)
    {
        if (MemoryMarshal.Read<int>(buffer[..4]) != unchecked((int)0x1cb5c415))
        {
            throw new InvalidOperationException();
        }

        _buff = MemoryMarshal.Cast<byte, double>(buffer);
        _offset = 1;
    }
    public readonly int Constructor => MemoryMarshal.Cast<double, int>(_buff)[0];
    private void SetConstructor(int constructor)
    {
        MemoryMarshal.Cast<double, int>(_buff)[0] = constructor;
    }
    public ReadOnlySpan<byte> ToReadOnlySpan() => MemoryMarshal.Cast<double, byte>(_buff)[..Length];
    public readonly int Count => MemoryMarshal.Cast<double, int>(_buff)[1];
    public readonly int Length => Count * 8 + 8;
    private void SetCount(int count)
    {
        MemoryMarshal.Cast<double, int>(_buff)[1] = count;
    }
    
    public static Span<byte> Read(Span<byte> data, int offset)
    {
        if (MemoryMarshal.Read<int>(data.Slice(offset,4)) != unchecked((int)0x1cb5c415))
        {
            throw new InvalidOperationException();
        }
        int count = MemoryMarshal.Read<int>(data.Slice(offset + 4, 4));
        int len = 8 + count * 8;
        if (offset + len > data.Length)
        {
            throw new InvalidOperationException();
        }
        return data.Slice(offset, len);
    }

    public static int ReadSize(Span<byte> data, int offset)
    {
        if (MemoryMarshal.Read<int>(data.Slice(offset,4)) != unchecked((int)0x1cb5c415))
        {
            throw new InvalidOperationException();
        }
        int count = MemoryMarshal.Read<int>(data.Slice(offset + 4, 4));
        return 8 + count * 8;
    }
    
    public void Append(double value)
    {
        if (_buff.Length == _offset)
        {
            var tmp = new Double[_buff.Length * 2];
            _buff.CopyTo(tmp);
            _buff = tmp;
        }
        MemoryMarshal.Cast<double, int>(_buff)[1]++;
        _buff[_offset++] = value;
    }
    public ref readonly double this[int index] => ref _buff[1 + index];
}