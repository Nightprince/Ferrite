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
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DotNext.Buffers;
using Ferrite.Crypto;
using Ferrite.TL;
using Ferrite.TL.slim;
using Ferrite.TL.slim.mtproto;
using Ferrite.Utils;

namespace Ferrite.Core.Execution.Functions;

public class ReqDhParamsFunc : ITLFunction
{
    private readonly IKeyProvider _keyProvider;
    private readonly ILogger _log;
    private readonly IRandomGenerator _random;
    private readonly int[] _gs = new int[] { 3, 4, 7 };
    //TODO: Maybe change the DH_PRIME
    private const string DhPrime = "C71CAEB9C6B1C9048E6C522F70F13F73980D40238E3E21C14934D037563D930F48198A0AA7C14058229493D22530F4DBFA336F6E0AC925139543AED44CCE7C3720FD51F69458705AC68CD4FE6B6B13ABDC9746512969328454F18FAF8C595F642477FE96BB2A941D5BCD1D4AC8CC49880708FA9B378E3C4F3A9060BEE67CF9A4A4A695811051907E162753B56B0F6B410DBA74D8A84B2A14B3144E0EF1284754FD17ED950D5965B4B9DD46582DB1178D169C6BC465B0D6FF9CA3928FEF5B9AE4E418FC15E83EBEA0F87FA9FF5EED70050DED2849F47BF959D956850CE929851F0D8115F635B105EE2E4E15D04B2454BF6F4FADF034B10403119CD8E3B92FCC5B";
    public ReqDhParamsFunc(IKeyProvider provider, IRandomGenerator generator, ILogger logger)
    {
        _keyProvider = provider;
        _random = generator;
        this._log = logger;
    }
    public ValueTask<TLBytes?> Process(TLBytes q, TLExecutionContext ctx)
    {
        return new ValueTask<TLBytes?>(ProcessInternal(new TL.slim.mtproto.ReqDhParams(q.AsSpan()), ctx));
    }

    private TLBytes? ProcessInternal(TL.slim.mtproto.ReqDhParams query, TLExecutionContext ctx)
    {
        var rsaKey = _keyProvider.GetKey(query.PublicKeyFingerprint);
        if (rsaKey == null)
        {
            var rpcError = new RpcError(-404, ""u8);
            _log.Debug("Could not obtain the RSA Key.");
            return rpcError.TLBytes;
        }
        if(!ctx.SessionData.ContainsKey("nonce") || 
                !ctx.SessionData.ContainsKey("server_nonce"))
        {
            var rpcError = new RpcError(-404, ""u8);
            _log.Debug("Session is empty.");
            return rpcError.TLBytes;
        }
        Memory<byte> data;
        byte[] sha256;
        RSAPad(query.EncryptedData.ToArray() ,rsaKey, out data, out sha256);

        if (!sha256.AsSpan().SequenceEqual(data.Span.Slice(224)))
        {
            _log.Debug("SHA256 did not match.");
            var rpcError = new TL.slim.mtproto.RpcError(-404, ""u8);
            return rpcError.TLBytes;
        }

        var constructor = MemoryMarshal.Read<int>(data.Span[32..]);
        
        var sessionNonce = (byte[])ctx.SessionData["nonce"];
        var sessionServerNonce = (byte[])ctx.SessionData["server_nonce"];
        if (constructor == Constructors.mtproto_PQInnerData)
        {
            var len = PQInnerData.ReadSize(data.Span, 32);
            var pQInnerData = new PQInnerData(data.Span.Slice(32, len));
            ctx.SessionData.Add("new_nonce", pQInnerData.NewNonce.ToArray());
            if (!query.Nonce.SequenceEqual(pQInnerData.Nonce) ||
                !query.Nonce.SequenceEqual(sessionNonce) ||
                !query.ServerNonce.SequenceEqual(pQInnerData.ServerNonce) ||
                !query.ServerNonce.SequenceEqual(sessionServerNonce))
            {
                var rpcError = new RpcError(-404, "Nonce values did not match."u8);
                return rpcError.TLBytes;
            }
            var inner_new_nonce = pQInnerData.NewNonce.ToArray();
            var newNonceServerNonce = SHA1.HashData((inner_new_nonce)
                .Concat((byte[])sessionServerNonce).ToArray());
            var serverNonceNewNonce = SHA1.HashData(((byte[])sessionServerNonce)
                .Concat(inner_new_nonce).ToArray());
            var newNonceNewNonce = SHA1.HashData((inner_new_nonce)
                .Concat(inner_new_nonce).ToArray());
            var tmpAesKey = newNonceServerNonce
                .Concat(serverNonceNewNonce.SkipLast(8)).ToArray();
            var tmpAesIV = serverNonceNewNonce.Skip(12)
                .Concat(newNonceNewNonce).Concat((inner_new_nonce).SkipLast(28)).ToArray();
            ctx.SessionData.Add("temp_aes_key", tmpAesKey.ToArray());
            ctx.SessionData.Add("temp_aes_iv", tmpAesIV.ToArray());
            using var answer = GenerateEncryptedAnswer(ctx, sessionNonce, sessionServerNonce, tmpAesKey, tmpAesIV);
            var serverDhParamsOk = new ServerDhParamsOk(query.Nonce, query.ServerNonce,answer.Memory.Span);
            
            return serverDhParamsOk.TLBytes;
        }
        else if (constructor == Constructors.mtproto_PQInnerDataDc)
        {
            var len = PQInnerDataDc.ReadSize(data.Span, 32);
            var pQInnerDataDc = new PQInnerDataDc(data.Span.Slice(32, len));
            ctx.SessionData.Add("new_nonce", pQInnerDataDc.NewNonce.ToArray());
            if (!query.Nonce.SequenceEqual(pQInnerDataDc.Nonce) ||
                !query.Nonce.SequenceEqual(sessionNonce) ||
                !query.ServerNonce.SequenceEqual(pQInnerDataDc.ServerNonce) ||
                !query.ServerNonce.SequenceEqual(sessionServerNonce))
            {
                var rpcError = new RpcError(-404, "Nonce values did not match."u8);
                return rpcError.TLBytes;
            }
            var inner_new_nonce = pQInnerDataDc.NewNonce.ToArray();
            var newNonceServerNonce = SHA1.HashData((inner_new_nonce)
                .Concat((byte[])sessionServerNonce).ToArray());
            var serverNonceNewNonce = SHA1.HashData(((byte[])sessionServerNonce)
                .Concat(inner_new_nonce).ToArray());
            var newNonceNewNonce = SHA1.HashData((inner_new_nonce)
                .Concat(inner_new_nonce).ToArray());
            var tmpAesKey = newNonceServerNonce
                .Concat(serverNonceNewNonce.SkipLast(8)).ToArray();
            var tmpAesIV = serverNonceNewNonce.Skip(12)
                .Concat(newNonceNewNonce).Concat((inner_new_nonce).SkipLast(28)).ToArray();
            ctx.SessionData.Add("temp_aes_key", tmpAesKey.ToArray());
            ctx.SessionData.Add("temp_aes_iv", tmpAesIV.ToArray());
            using var answer = GenerateEncryptedAnswer(ctx, sessionNonce, sessionServerNonce, tmpAesKey, tmpAesIV);
            var serverDhParamsOk = new ServerDhParamsOk(query.Nonce, query.ServerNonce,answer.Memory.Span);
            return serverDhParamsOk.TLBytes;
        }
        else if (constructor == Constructors.mtproto_PQInnerDataTempDc)
        {
            var len = PQInnerDataTempDc.ReadSize(data.Span, 32);
            var pQInnerDataTempDc = new PQInnerDataTempDc(data.Span.Slice(32, len));
            ctx.SessionData.Add("temp_auth_key", true);
            ctx.SessionData.Add("temp_auth_key_expires_in", pQInnerDataTempDc.ExpiresIn);
            ctx.SessionData.Add("new_nonce", pQInnerDataTempDc.NewNonce.ToArray());
            if (!query.Nonce.SequenceEqual(pQInnerDataTempDc.Nonce) ||
                !query.Nonce.SequenceEqual(sessionNonce) ||
                !query.ServerNonce.SequenceEqual(pQInnerDataTempDc.ServerNonce) ||
                !query.ServerNonce.SequenceEqual(sessionServerNonce))
            {
                var rpcError = new RpcError(-404, "Nonce values did not match."u8);
                return rpcError.TLBytes;
            }
            var inner_new_nonce = pQInnerDataTempDc.NewNonce.ToArray();
            var newNonceServerNonce = SHA1.HashData((inner_new_nonce)
                .Concat((byte[])sessionServerNonce).ToArray());
            var serverNonceNewNonce = SHA1.HashData(((byte[])sessionServerNonce)
                .Concat(inner_new_nonce).ToArray());
            var newNonceNewNonce = SHA1.HashData((inner_new_nonce)
                .Concat(inner_new_nonce).ToArray());
            var tmpAesKey = newNonceServerNonce
                .Concat(serverNonceNewNonce.SkipLast(8)).ToArray();
            var tmpAesIV = serverNonceNewNonce.Skip(12)
                .Concat(newNonceNewNonce).Concat((inner_new_nonce).SkipLast(28)).ToArray();
            ctx.SessionData.Add("temp_aes_key", tmpAesKey.ToArray());
            ctx.SessionData.Add("temp_aes_iv", tmpAesIV.ToArray());
            using var answer = GenerateEncryptedAnswer(ctx, sessionNonce, sessionServerNonce, tmpAesKey, tmpAesIV);
            ctx.SessionData.Add("valid_until", DateTime.Now.AddSeconds(pQInnerDataTempDc.ExpiresIn));
            var serverDhParamsOk = new ServerDhParamsOk(query.Nonce, query.ServerNonce,answer.Memory.Span);
            return serverDhParamsOk.TLBytes;
        }
        else if (constructor == Constructors.mtproto_PQInnerDataTemp)
        {
            var len = PQInnerDataTemp.ReadSize(data.Span, 32);
            var pQInnerDataTemp = new PQInnerDataTemp(data.Span.Slice(32, len));
            ctx.SessionData.Add("temp_auth_key", true);
            ctx.SessionData.Add("temp_auth_key_expires_in", pQInnerDataTemp.ExpiresIn);
            ctx.SessionData.Add("new_nonce", pQInnerDataTemp.NewNonce.ToArray());
            if (!query.Nonce.SequenceEqual(pQInnerDataTemp.Nonce) ||
                !query.Nonce.SequenceEqual(sessionNonce) ||
                !query.ServerNonce.SequenceEqual(pQInnerDataTemp.ServerNonce) ||
                !query.ServerNonce.SequenceEqual(sessionServerNonce))
            {
                var rpcError = new RpcError(-404, "Nonce values did not match."u8);
                return rpcError.TLBytes;
            }
            var inner_new_nonce = pQInnerDataTemp.NewNonce.ToArray();
            var newNonceServerNonce = SHA1.HashData((inner_new_nonce)
                .Concat((byte[])sessionServerNonce).ToArray());
            var serverNonceNewNonce = SHA1.HashData(((byte[])sessionServerNonce)
                .Concat(inner_new_nonce).ToArray());
            var newNonceNewNonce = SHA1.HashData((inner_new_nonce)
                .Concat(inner_new_nonce).ToArray());
            var tmpAesKey = newNonceServerNonce
                .Concat(serverNonceNewNonce.SkipLast(8)).ToArray();
            var tmpAesIV = serverNonceNewNonce.Skip(12)
                .Concat(newNonceNewNonce).Concat((inner_new_nonce).SkipLast(28)).ToArray();
            ctx.SessionData.Add("temp_aes_key", tmpAesKey.ToArray());
            ctx.SessionData.Add("temp_aes_iv", tmpAesIV.ToArray());
            using var answer = GenerateEncryptedAnswer(ctx, sessionNonce, sessionServerNonce, tmpAesKey, tmpAesIV);
            ctx.SessionData.Add("valid_until", DateTime.Now.AddSeconds(pQInnerDataTemp.ExpiresIn));
            var serverDhParamsOk = new ServerDhParamsOk(query.Nonce, query.ServerNonce,answer.Memory.Span);
            return serverDhParamsOk.TLBytes;
        }
        return null;
    }
    private IMemoryOwner<byte> GenerateEncryptedAnswer(TLExecutionContext ctx, byte[] sessionNonce, byte[] sessionServerNonce, byte[] tmpAesKey, byte[] tmpAesIV)
    {
        BigInteger prime = BigInteger.Parse("0"+DhPrime, NumberStyles.HexNumber);
        BigInteger min = BigInteger.Pow(new BigInteger(2), 2048 - 64);
        BigInteger max = prime - min;
        BigInteger a = _random.GetRandomInteger(2, prime - 2);
        BigInteger g = new BigInteger(_gs[_random.GetRandomNumber(_gs.Length)]);
        BigInteger g_a = BigInteger.ModPow(g, a, prime);
        while (g_a <= min || g_a >= max)
        {
            a = _random.GetRandomInteger(2, prime - 2);
            g_a = BigInteger.ModPow(g, a, prime);
        }
        
        var innerNonce = sessionNonce;
        var innerServerNonce = sessionServerNonce;
        var innerDhPrime = prime.ToByteArray(true,true);
        var innerG = (int)g;
        var innerGA = g_a.ToByteArray(true, true);
        var innerServerTime = (int)DateTimeOffset.Now.ToUnixTimeSeconds();

        using var serverDhInnerData = new ServerDhInnerData(innerNonce, innerServerNonce, innerG,
            innerDhPrime, innerGA, innerServerTime);
        
        ctx.SessionData.Add("g", innerG);
        ctx.SessionData.Add("a", a.ToByteArray(true,true));
        ctx.SessionData.Add("g_a", innerGA);
        int len = 20 + serverDhInnerData.Length;
        while (len % 16 != 0)
        {
            len++;
        }

        var answerWithHash = UnmanagedMemoryAllocator.Allocate<byte>(len);
        var innerSpan = serverDhInnerData.ToReadOnlySpan();
        SHA1.HashData( innerSpan, answerWithHash.Span[..20]);
        innerSpan.CopyTo(answerWithHash.Span[20..]);

        Aes aes = Aes.Create();
        aes.Key = tmpAesKey;
        aes.EncryptIge(answerWithHash.Span, tmpAesIV);
        return answerWithHash;
    }

    private void RSAPad(byte[] encryptedData, IRSAKey rsaKey, out Memory<byte> data, out byte[] sha256)
    {
        data = rsaKey.DecryptBlock(encryptedData).AsMemory();
        // data: |-temp_key_xor(32)-|-|-aes_encrypted(224)-| 256 bytes
        Span<byte> tempKey = data.Slice(0, 32).Span;
        Span<byte> aesEncrypted = data.Slice(32).Span;

        byte[] sha256AesEncrypted = SHA256.HashData(aesEncrypted);
        for (int i = 0; i < 32; i++)
        {
            tempKey[i] = (byte)(tempKey[i] ^ sha256AesEncrypted[i]);
        }
        // data: |-temp_key(32)+aes_encrypted(224)-| 256 bytes
        Aes aes = Aes.Create();
        aes.Key = tempKey.ToArray();
        aes.DecryptIge(aesEncrypted, stackalloc byte[32]);
        // data: |-temp_key(32)+data_with_hash(224)-| 256 bytes
        // data_with_hash: |-data_pad_reversed(192)+
        //                   SHA256(temp_key+data_pad)(32)-| 256 bytes
        Span<byte> dataPadReversed = aesEncrypted.Slice(0, 192);
        dataPadReversed.Reverse();
        // data: |-temp_key(32)+data_pad(192)+
        //                   SHA256(temp_key+data_pad)(32)-| 256 bytes
        sha256 = SHA256.HashData(data.Slice(0, 224).Span);
    }
}