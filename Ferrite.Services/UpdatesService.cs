﻿//
//  Project Ferrite is an Implementation of the Telegram Server API
//  Copyright 2022 Aykut Alparslan KOC <aykutalparslan@msn.com>
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Affero General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU Affero General Public License for more details.
//
//  You should have received a copy of the GNU Affero General Public License
//  along with this program.  If not, see <https://www.gnu.org/licenses/>.
//
using System;
using Ferrite.Data;
using Ferrite.Data.Updates;
using Ferrite.Utils;

namespace Ferrite.Services;

public class UpdatesService : IUpdatesService
{
    private readonly IMTProtoTime _time;
    private readonly ISessionService _sessions;
    private readonly IDistributedPipe _pipe;
    private readonly IPersistentStore _store;
    private readonly IDistributedCache _cache;
    public UpdatesService(IMTProtoTime time, ISessionService sessions, IDistributedPipe pipe,
        IPersistentStore store, IDistributedCache cache)
    {
        _time = time;
        _sessions = sessions;
        _pipe = pipe;
        _store = store;
        _cache = cache;
    }

    public async Task<StateDTO> GetState(long authKeyId)
    {
        var auth = await _store.GetAuthorizationAsync(authKeyId);
        var counter = _cache.GetCounter(auth.UserId + "_pts");
        int pts = (int)await counter.Get();
        return new StateDTO()
        {
            Date = (int)_time.GetUnixTimeInSeconds(),
            Pts = pts,
            Seq = pts//TODO: fix seq
        };
    }
}

