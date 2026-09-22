using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private static readonly byte[] FriendBridgeMagic = "FIFR"u8.ToArray();
    private const int FriendBridgeMaximumEnvelopeLength = 1024 * 1024;
    private const int FriendBridgeMaximumTextBytes = 1024;
    private readonly ConcurrentDictionary<long, FriendBridgeSubscriber> _friendSubscribers = new();
    private readonly ConcurrentDictionary<string, byte> _friendRequestDeliveries = new(StringComparer.Ordinal);

    private sealed class FriendBridgeSubscriber(NetworkStream stream, uint virtualId)
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        public uint VirtualId { get; } = virtualId;

        public async Task<bool> SendAsync(uint eventCode, byte[] envelope, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                var header = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), eventCode);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), (uint)envelope.Length);
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(envelope, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }

    private sealed record FriendBridgeIdentity(
        string Username,
        string Password,
        string CharacterName,
        uint VirtualId,
        WorldPresence Presence);

    private async Task HandleFriendBridgeAsync(
        NetworkStream stream,
        string channel,
        int port,
        string remote,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        try
        {
            var control = new byte[8];
            if (!await ReadExactlyAsync(stream, control, cancellationToken))
                return;
            var version = BinaryPrimitives.ReadUInt32LittleEndian(control.AsSpan(0, 4));
            var mode = BinaryPrimitives.ReadUInt32LittleEndian(control.AsSpan(4, 4));
            if (version != FriendProtocol.BridgeVersion
                || mode is not (FriendProtocol.BridgeCall or FriendProtocol.BridgeSubscribe))
            {
                await WriteFriendBridgeResponseAsync(stream, 2, [], cancellationToken);
                _log($"{channel}:{port} {remote} 好友桥接协议版本或模式无效 version={version} mode={mode}");
                return;
            }

            var identity = await ReadAndAuthenticateFriendBridgeAsync(stream, remoteIp, cancellationToken);
            if (identity is null)
            {
                await WriteFriendBridgeResponseAsync(stream, 1, [], cancellationToken);
                _log($"{channel}:{port} {remote} 好友桥接身份校验失败");
                return;
            }

            if (mode == FriendProtocol.BridgeSubscribe)
            {
                await WriteFriendBridgeResponseAsync(stream, FriendProtocol.BridgeStatusSuccess, [], cancellationToken);
                var subscriber = new FriendBridgeSubscriber(stream, identity.VirtualId);
                _friendSubscribers.AddOrUpdate(identity.Presence.CharacterId, subscriber, (_, _) => subscriber);
                _log($"{channel}:{port} {remote} 好友事件订阅已建立 character={identity.CharacterName}");
                var pending = await _database.GetPendingFriendNotificationsAsync(
                    identity.Presence.CharacterId, cancellationToken);
                foreach (var notification in pending)
                    await SendFriendRequestEventAsync(identity.Presence, subscriber, notification, cancellationToken);
                try
                {
                    var disconnectProbe = new byte[1];
                    while (await stream.ReadAsync(disconnectProbe, cancellationToken) != 0)
                    {
                        // The subscription is adapter-to-client only. Any input is ignored.
                    }
                }
                finally
                {
                    _friendSubscribers.TryRemove(
                        new KeyValuePair<long, FriendBridgeSubscriber>(identity.Presence.CharacterId, subscriber));
                }
                return;
            }

            var callHeader = new byte[8];
            if (!await ReadExactlyAsync(stream, callHeader, cancellationToken))
                return;
            var functionCode = BinaryPrimitives.ReadUInt32LittleEndian(callHeader.AsSpan(0, 4));
            var envelopeLength = BinaryPrimitives.ReadUInt32LittleEndian(callHeader.AsSpan(4, 4));
            if (!FriendProtocol.IsFriendFunction(functionCode)
                || envelopeLength is < 20 or > FriendBridgeMaximumEnvelopeLength)
            {
                await WriteFriendBridgeResponseAsync(stream, 2, [], cancellationToken);
                return;
            }
            var envelope = new byte[envelopeLength];
            if (!await ReadExactlyAsync(stream, envelope, cancellationToken))
                return;
            var response = await HandleFriendFunctionAsync(identity, functionCode, envelope, cancellationToken);
            await WriteFriendBridgeResponseAsync(stream, FriendProtocol.BridgeStatusSuccess, response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _log($"{channel}:{port} {remote} 好友桥接断开：{ex.Message}");
        }
        catch (Exception ex)
        {
            _log($"{channel}:{port} {remote} 好友桥接异常：{ex.Message}");
            try { await WriteFriendBridgeResponseAsync(stream, 3, [], cancellationToken); }
            catch { }
        }
    }

    private async Task<FriendBridgeIdentity?> ReadAndAuthenticateFriendBridgeAsync(
        NetworkStream stream,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        var username = await ReadFriendBridgeStringAsync(stream, cancellationToken);
        var password = await ReadFriendBridgeStringAsync(stream, cancellationToken);
        var characterName = await ReadFriendBridgeStringAsync(stream, cancellationToken);
        var virtualIdBytes = new byte[4];
        if (username is null || password is null || characterName is null
            || !await ReadExactlyAsync(stream, virtualIdBytes, cancellationToken))
            return null;
        var virtualId = BinaryPrimitives.ReadUInt32LittleEndian(virtualIdBytes);
        if (virtualId == 0)
            virtualId = FriendProtocol.DefaultVirtualId;
        var authorization = await _database.VerifyArchiveAuthorizationAsync(username, password, cancellationToken);
        if (!authorization.Success)
            return null;
        var presence = _activeWorldSessions.Values.FirstOrDefault(item =>
            item.OnlineSinceUtc <= DateTime.UtcNow
            && string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.CharacterName, characterName, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(remoteIp)
                || string.Equals(item.RemoteIp, remoteIp, StringComparison.OrdinalIgnoreCase)));
        return presence is null
            ? null
            : new FriendBridgeIdentity(username, password, characterName, virtualId, presence);
    }

    private async Task<byte[]> HandleFriendFunctionAsync(
        FriendBridgeIdentity identity,
        uint functionCode,
        byte[] envelope,
        CancellationToken cancellationToken)
    {
        if (!FriendProtocol.TryOpenCall(envelope, out _, out var body))
            return FriendProtocol.BuildFunctionResponse(false);
        var ownerId = identity.Presence.CharacterId;
        bool success;

        switch (functionCode)
        {
            case FriendProtocol.GetFriendList:
                if (!FriendProtocol.TryReadVirtualKey(body, out var listOwner)
                    || !body.IsComplete
                    || !IsOwnedVirtualKey(listOwner, identity.VirtualId))
                    return FriendProtocol.BuildFunctionResponse(false);
                var snapshot = await _database.GetFriendListSnapshotAsync(ownerId, cancellationToken);
                return FriendProtocol.BuildFriendListResponse(snapshot, IsCharacterOnline, identity.VirtualId);

            case FriendProtocol.GetFriendInfo:
                if (!FriendProtocol.TryReadFriendKey(body, out var infoKey)
                    || !body.IsComplete
                    || !IsOwnedFriendKey(infoKey, identity.VirtualId, out var infoCharacterId))
                    return FriendProtocol.BuildFunctionResponse(false);
                var friend = await _database.GetFriendInfoAsync(ownerId, infoCharacterId, cancellationToken);
                return FriendProtocol.BuildFriendInfoResponse(friend, IsCharacterOnline(infoCharacterId), identity.VirtualId);

            case FriendProtocol.RequestNewFriend:
                if (!FriendProtocol.TryReadRequestData(body, out var request)
                    || !body.IsComplete
                    || !FriendProtocol.IsExpectedGameCode(request.RequesteeGameCode)
                    || !IsOwnedVirtualKey(request.FromVirtual, identity.VirtualId)
                    || !FriendProtocol.IsExpectedGameCode(request.ToVirtual.GameCode)
                    || (!string.IsNullOrEmpty(request.FromLoginId)
                        && !string.Equals(request.FromLoginId, identity.Username, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(request.FromNickName)
                        && !string.Equals(request.FromNickName, identity.CharacterName, StringComparison.OrdinalIgnoreCase)))
                    return FriendProtocol.BuildFunctionResponse(false);
                var created = await _database.CreateFriendRequestAsync(
                    ownerId, request.RequestId, request.Message, request.AddToNxFriend, cancellationToken);
                if (created.Success)
                {
                    var notification = new FriendRequestNotification(
                        created.SerialNo,
                        request.RequestId,
                        identity.Username,
                        identity.CharacterName,
                        identity.VirtualId,
                        0,
                        request.Message,
                        request.AddToNxFriend);
                    await SendFriendRequestEventAsync(created.RequesteeCharacterId, notification, cancellationToken);
                    RaiseFriendStateChanged();
                }
                return FriendProtocol.BuildFunctionResponse(created.Success);

            case FriendProtocol.ConfirmNewFriend:
                if (!body.TryReadUInt32(out var serialNo)
                    || !body.TryReadByte(out var confirmCode)
                    || !body.TryReadUInt32(out var insertCategoryCode)
                    || !body.IsComplete)
                    return FriendProtocol.BuildFunctionResponse(false);
                success = await _database.ConfirmFriendRequestAsync(
                    ownerId, serialNo, confirmCode, insertCategoryCode, cancellationToken);
                if (success && confirmCode != FriendProtocol.ConfirmLater)
                    RaiseFriendStateChanged();
                return FriendProtocol.BuildFunctionResponse(success);

            case FriendProtocol.BlockFriend:
                if (!FriendProtocol.TryReadFriendKey(body, out var blockKey)
                    || !body.TryReadByte(out var blockValue)
                    || blockValue > 1
                    || !body.IsComplete
                    || !IsOwnedFriendKey(blockKey, identity.VirtualId, out var blockCharacterId))
                    return FriendProtocol.BuildFunctionResponse(false);
                success = await _database.SetFriendBlockedAsync(ownerId, blockCharacterId, blockValue != 0, cancellationToken);
                if (success) RaiseFriendStateChanged();
                return FriendProtocol.BuildFunctionResponse(success);

            case FriendProtocol.ChangeFriendMemo:
                if (!body.TryReadUInt64(out var memoIdCode)
                    || !body.TryReadString(31, out var memo)
                    || !body.IsComplete
                    || !TryResolveIdCode(memoIdCode, out var memoCharacterId))
                    return FriendProtocol.BuildFunctionResponse(false);
                success = await _database.ChangeFriendMemoAsync(ownerId, memoCharacterId, memo, cancellationToken);
                if (success) RaiseFriendStateChanged();
                return FriendProtocol.BuildFunctionResponse(success);

            case FriendProtocol.AddFriendToCategory:
            case FriendProtocol.DeleteFriendFromCategory:
                if (!FriendProtocol.TryReadFriendKey(body, out var categoryFriendKey)
                    || !body.TryReadUInt32(out var categoryCode)
                    || !body.TryReadByte(out var systemCall)
                    || systemCall > 1
                    || !body.IsComplete
                    || !IsOwnedFriendKey(categoryFriendKey, identity.VirtualId, out var categoryFriendId))
                    return FriendProtocol.BuildFunctionResponse(false);
                success = functionCode == FriendProtocol.AddFriendToCategory
                    ? await _database.AddFriendToCategoryAsync(ownerId, categoryFriendId, categoryCode, cancellationToken)
                    : await _database.DeleteFriendFromCategoryAsync(ownerId, categoryFriendId, categoryCode, cancellationToken);
                if (success) RaiseFriendStateChanged();
                return FriendProtocol.BuildFunctionResponse(success);

            case FriendProtocol.MoveFriendCategory:
                if (!FriendProtocol.TryReadFriendKey(body, out var moveFriendKey)
                    || !body.TryReadUInt32(out var fromCategory)
                    || !body.TryReadUInt32(out var toCategory)
                    || !body.TryReadByte(out var moveSystemCall)
                    || moveSystemCall > 1
                    || !body.IsComplete
                    || !IsOwnedFriendKey(moveFriendKey, identity.VirtualId, out var moveFriendId))
                    return FriendProtocol.BuildFunctionResponse(false);
                success = await _database.MoveFriendCategoryAsync(
                    ownerId, moveFriendId, fromCategory, toCategory, cancellationToken);
                if (success) RaiseFriendStateChanged();
                return FriendProtocol.BuildFunctionResponse(success);

            case FriendProtocol.AddCategory:
                if (!FriendProtocol.TryReadVirtualKey(body, out var categoryOwner)
                    || !body.TryReadString(31, out var categoryName)
                    || !body.TryReadUInt32(out var property)
                    || !body.TryReadUInt32(out var allowType)
                    || !body.IsComplete
                    || !IsOwnedVirtualKey(categoryOwner, identity.VirtualId))
                    return FriendProtocol.BuildFunctionResponse(false);
                success = await _database.AddFriendCategoryAsync(ownerId, categoryName, property, allowType, cancellationToken);
                if (success) RaiseFriendStateChanged();
                return FriendProtocol.BuildFunctionResponse(success);

            case FriendProtocol.DeleteCategory:
                if (!body.TryReadUInt32(out var deleteCategory)
                    || !body.TryReadByte(out var deleteSystemCall)
                    || deleteSystemCall > 1
                    || !body.IsComplete)
                    return FriendProtocol.BuildFunctionResponse(false);
                success = await _database.DeleteFriendCategoryAsync(ownerId, deleteCategory, cancellationToken);
                if (success) RaiseFriendStateChanged();
                return FriendProtocol.BuildFunctionResponse(success);

            case FriendProtocol.ChangeCategoryName:
                if (!body.TryReadUInt32(out var renameCategory)
                    || !body.TryReadString(31, out var newName)
                    || !body.TryReadByte(out var renameSystemCall)
                    || renameSystemCall > 1
                    || !body.IsComplete)
                    return FriendProtocol.BuildFunctionResponse(false);
                success = await _database.ChangeFriendCategoryNameAsync(ownerId, renameCategory, newName, cancellationToken);
                if (success) RaiseFriendStateChanged();
                return FriendProtocol.BuildFunctionResponse(success);

            case FriendProtocol.ChangeCategoryProperty:
            case FriendProtocol.ChangeCategoryAllowType:
                if (!body.TryReadUInt32(out var changedCategory)
                    || !body.TryReadUInt32(out var changedValue)
                    || !body.TryReadByte(out var changedSystemCall)
                    || changedSystemCall > 1
                    || !body.IsComplete)
                    return FriendProtocol.BuildFunctionResponse(false);
                success = functionCode == FriendProtocol.ChangeCategoryProperty
                    ? await _database.ChangeFriendCategoryPropertyAsync(ownerId, changedCategory, changedValue, cancellationToken)
                    : await _database.ChangeFriendCategoryAllowTypeAsync(ownerId, changedCategory, changedValue, cancellationToken);
                if (success) RaiseFriendStateChanged();
                return FriendProtocol.BuildFunctionResponse(success);
        }
        return FriendProtocol.BuildFunctionResponse(false);
    }

    private bool IsCharacterOnline(long characterId)
        => _activeWorldSessions.Values.Any(item => item.CharacterId == characterId);

    private static bool IsOwnedVirtualKey(VirtualKey key, uint virtualId)
        => FriendProtocol.IsExpectedGameCode(key.GameCode)
           && (key.VirtualId == 0 || key.VirtualId == virtualId);

    private static bool IsOwnedFriendKey(FriendKey key, uint virtualId, out long friendCharacterId)
    {
        friendCharacterId = 0;
        if (!FriendProtocol.IsExpectedGameCode(key.OwnerGameCode)
            || (key.OwnerVirtualId != 0 && key.OwnerVirtualId != virtualId)
            || !FriendProtocol.IsExpectedGameCode(key.FriendGameCode)
            || !TryResolveIdCode(key.IdCode, out friendCharacterId))
            return false;
        return true;
    }

    private static bool TryResolveIdCode(ulong idCode, out long characterId)
    {
        characterId = 0;
        if (!FriendProtocol.IsExpectedGameCode((uint)idCode))
            return false;
        characterId = FriendProtocol.CharacterIdFromIdCode(idCode);
        return characterId > 0;
    }

    private async Task SendFriendRequestEventAsync(
        long requesteeCharacterId,
        FriendRequestNotification request,
        CancellationToken cancellationToken)
    {
        if (!_friendSubscribers.TryGetValue(requesteeCharacterId, out var subscriber))
            return;
        var presence = _activeWorldSessions.Values.FirstOrDefault(item => item.CharacterId == requesteeCharacterId);
        if (presence is null)
            return;
        await SendFriendRequestEventAsync(presence, subscriber, request, cancellationToken);
    }

    private async Task SendFriendRequestEventAsync(
        WorldPresence presence,
        FriendBridgeSubscriber subscriber,
        FriendRequestNotification request,
        CancellationToken cancellationToken)
    {
        var deliveryKey = $"{presence.SessionId}:{request.SerialNo}";
        if (!_friendRequestDeliveries.TryAdd(deliveryKey, 0))
            return;
        var envelope = FriendProtocol.BuildRequestEvent(request, subscriber.VirtualId);
        if (!await subscriber.SendAsync(FriendProtocol.RequestNewFriendEvent, envelope, cancellationToken))
        {
            _friendRequestDeliveries.TryRemove(deliveryKey, out _);
            _friendSubscribers.TryRemove(new KeyValuePair<long, FriendBridgeSubscriber>(presence.CharacterId, subscriber));
        }
    }

    private void RaiseFriendStateChanged()
    {
        try { FriendStateChanged?.Invoke(); }
        catch (Exception ex) { _log($"好友后台刷新通知异常：{ex.Message}"); }
    }

    private static async Task<string?> ReadFriendBridgeStringAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        if (!await ReadExactlyAsync(stream, lengthBytes, cancellationToken))
            return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (length > FriendBridgeMaximumTextBytes)
            return null;
        var bytes = new byte[length];
        if (!await ReadExactlyAsync(stream, bytes, cancellationToken))
            return null;
        var value = Encoding.UTF8.GetString(bytes);
        return value.IndexOf('\0') >= 0 ? null : value;
    }

    private static async Task WriteFriendBridgeResponseAsync(
        NetworkStream stream,
        uint status,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), status);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        if (payload.Length != 0)
            await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
