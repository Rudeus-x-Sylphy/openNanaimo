using System.IO;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService : IAsyncDisposable
{
    private const int NativeLoginPayloadLength = 40;
    private const int NativeLoginUsernameLength = 16;
    private const int NativeLoginPasswordLength = 16;
    private const int CharacterCreationPayloadLength = 52;
    private const int FriendRecommendationRequestPayloadLength = 16;
    private const int FriendRecommendationResponsePayloadLength = 20;
    private const uint FriendRecommendationNonexistent = 10;
    private const uint FriendRecommendationSelf = 20;
    private const uint FriendRecommendationSuccess = 30;
    private const int PostLoginPayloadLength = 24;
    private const int ChannelListRequestPayloadLength = 4;
    private const int ArenaAdapterRequestPayloadLength = 4;
    private const int ArenaAdapterResponsePayloadLength = 24;
    private const int ArenaAdapterAddressLength = 16;
    private const int ArenaGameAdapterFirstPort = 22051;
    private const int ArenaGameAdapterTypeCount = 4;
    private const int RestrictionCheckPayloadLength = 16;
    private const int WorldConnectPayloadLength = 16;
    private const int TownEnterRequestPayloadLength = 10;
    private const int RoomEnterRequestPayloadLength = 8;
    private const int TownUserInfoRequestPayloadLength = 4;
    private const int TownLeavePayloadLength = 4;
    private const int TownMapMarkerPayloadLength = 78;
    private const int SceneEmotionPayloadLength = 4;
    private const int SceneChatPayloadLength = 100;
    private const int TradeInviteRequestPayloadLength = 4;
    private const int TradeAgreementPayloadLength = 20;
    private const int TradeCreateRequestPayloadLength = 8;
    private const int TradeEnterRequestPayloadLength = 8;
    private const int TradePeerInfoPayloadLength = 56;
    private const int TradeOfferRequestPayloadLength = 24;
    private const int TradeOfferResultPayloadLength = 12;
    private const int TradeReadyPayloadLength = 48;
    private const int TradeCardSlotCount = 5;
    private const int TradeCardRecordLength = 8;
    private const int PartyInvitationPayloadLength = 24;
    private const int PartyAgreementPayloadLength = 4;
    private const int PartyOwnerChangeRequestPayloadLength = 4;
    private const int PartyMemberRemoveRequestPayloadLength = 4;
    private const int PartyLeaveRequestPayloadLength = 0;
    private const int PartyMemberRecordLength = 24;
    private const int PartyMaximumMembers = 3;
    private const ushort PartyAgreementRefused = 0;
    private const ushort PartyAgreementAccepted = 1;
    private const ushort PartyAgreementUnavailable = 2;
    private const ushort TradeAgreementAccepted = 10;
    private const ushort TradeAgreementRefused = 20;
    private const ushort TradeAgreementNotReady = 30;
    private const int TradePutCard = 20;
    private const int TradeRemoveCard = 10;
    private const int TradePutHans = 30;
    private const uint TradeResultSuccess = 10;
    private const uint TradeResultPeerUnavailable = 20;
    private const uint TradeResultCardOverflow = 30;
    private const uint TradeResultHansOverflow = 40;
    private const uint TradeResultFailed = 70;
    private const int AntiBotSmallPayloadLength = 1028;
    private const int AntiBotSmallDataCapacity = 1024;
    private const int LegacyTencentLoginPayloadLength = 360;
    private const int LegacyTencentToken1Length = 256;
    private const int LegacyTencentToken2Length = 100;
    private const int MainGuideEventPayloadLength = 20;
    private const int MainGuideIdentityLength = 16;
    private const int CardListRequestPayloadLength = 4;
    private const int CardListResponsePayloadLength = 0x8C - 8;
    private const int CardSummonRequestPayloadLength = 0;
    private const int CardSellRequestPayloadLength = 8;
    private const int CardUnionRequestPayloadLength = 20;
    private const int SkillUpgradeRequestPayloadLength = 4;
    private const int SkillUpgradeResponsePayloadLength = 8;
    private const int SkillSlotRequestPayloadLength = 8;
    private const int SkillSlotResponsePayloadLength = 12;
    private const int DungeonSkillUseRequestPayloadLength = 4;
    private const int DungeonSkillUseResponsePayloadLength = 8;
    private const int DungeonQuickItemUseRequestPayloadLength = 4;
    private const int DungeonQuickItemUseResponsePayloadLength = 12;
    private const int InventoryExpansionRequestPayloadLength = 4;
    private const int InventoryExpansionResponsePayloadLength = 12;
    private const int FaceCouponRequestPayloadLength = 68;
    private const int FaceCouponResponsePayloadLength = 44;
    private const ushort CardUnionType = 10;
    private const uint CardUnionSuccessResult = 400;
    private const int AuctionListRequestPayloadLength = 24;
    private const int AuctionListEntryLength = 24;
    private const int AuctionListEntryCapacity = 12;
    private const int AuctionListResponsePayloadLength = 8 + AuctionListEntryLength * AuctionListEntryCapacity;
    private const int AuctionBuyRequestPayloadLength = 24;
    private const int AuctionRegisterRequestPayloadLength = 12;
    private const int AuctionRegisterResponsePayloadLength = 12;
    private const int AuctionRetrievalRequestPayloadLength = 16;
    // The retail C5B1 consumer only closes the type-9 trade wait popup after
    // its base UI active flag (+0x15) has been set. A loopback round trip can
    // otherwise arrive before the next UI updates and leave a modal input
    // shield alive for the rest of the client session.
    private static readonly TimeSpan AuctionUiResponseDelay = TimeSpan.FromMilliseconds(50);
    private const int EmptyInventoryRequestPayloadLength = 0;
    private const int AvatarInventoryRecordLength = 12;
    private const int AvatarInventoryCapacity = 56;
    private const int AvatarDeleteRequestPayloadLength = 12;
    private const int AvatarDeleteResponsePayloadLength = 12;
    private const int InteriorInventoryRequestPayloadLength = 4;
    private const int InteriorInventoryRecordLength = 12;
    private const int InteriorInventoryCapacity = 84;
    private const int InteriorDeleteRequestPayloadLength = 12;
    private const int InteriorDeleteResponsePayloadLength = 12;
    private const int GameItemDeleteRequestPayloadLength = 8;
    private const int GameItemDeleteResponsePayloadLength = 12;
    private const int CashInventoryRequestPayloadLength = 4;
    private const int CashInboxClaimRequestPayloadLength = 12;
    private const int TokenInventoryRequestPayloadLength = 0;
    private const int TokenDeleteRequestPayloadLength = 8;
    private const int TokenUseRequestPayloadLength = 8;
    private const int PetInventoryRecordLength = 36;
    private const int PetChangeRequestPayloadLength = 16;
    private const int PetDeleteRequestPayloadLength = 36;
    private const int PetDeleteResponsePayloadLength = 16;
    private const int InventoryChangeRequestPayloadLength = 136;
    private const int InventoryChangeResponsePayloadLength = 40;
    private const int TaskActivationRequestPayloadLength = 8;
    private const int TaskAbandonRequestPayloadLength = 8;
    private const int TaskCompletionRequestPayloadLength = 80;
    private const int QuestScrollPurchaseRequestPayloadLength = 4;
    private const int TaskMutationResponsePayloadLength = 8;
    private const int TaskCompletionResponsePayloadLength = 28;
    private const int QuestScrollPurchaseResponsePayloadLength = 16;
    private const int TaskRecordLength = 20;
    private const int NormalTaskCapacity = 10;
    private const int TaskListResponsePayloadLength = 244;
    private const int P2PMyInfoRequestPayloadLength = 20;
    private const int P2PIpAddressLength = 16;
    private const int P2PPeerRecordLength = 24;
    private const int P2PProtocolRequestPayloadLength = 4;
    private const int GameRoomUserInfoRequestPayloadLength = 4;
    private const int GameRoomSlotChangeRequestPayloadLength = 4;
    private const int FlyshootingGameDataRequestPayloadLength = 4;
    private const int FlyshootingGameDataResponsePayloadLength = 0x328 - 8;
    // The retail round trip keeps the ready/team control alive until the mouse
    // click has been released. A loopback response in the same UI frame swaps
    // that control early and lets the underlying chat control consume the click.
    private const int MulticastGameEventPayloadLength = 20;
    private const int ShootingSyncPayloadLength = 32;
    private const int DungeonCollisionPayloadLength = 12;
    private const int DungeonBossRecordingPayloadLength = 24;
    private const int DungeonPickupPayloadLength = 8;
    private const int DungeonLogicFramesPerSecond = 60;
    private const int DungeonEndGamePayloadLength = 4;
    private const int DungeonResettingPayloadLength = 4;
    private const int DungeonContinueCountResponsePayloadLength = 4;
    private const int DungeonContinueRequestPayloadLength = 4;
    private const int DungeonContinueResponsePayloadLength = 16;
    private const byte DungeonInitialContinueCount = 3;
    private static readonly ushort[] NormalDungeonContinueCosts =
    [
        10, 50, 130, 200, 290, 320, 360, 530, 580, 690, 750, 860, 950,
        1060, 1070, 1280,
        1400, 1400, 1400, 1400, 1400, 1400, 1400, 1400, 1400, 1400, 1400,
        1400, 1400, 1400, 1400, 1400, 1400, 1400, 1400, 1400, 1400, 1400, 1400
    ];
    private static readonly ushort[] SpecialDungeonContinueCosts = [300, 800, 1200, 1600];
    internal static bool IsKnownDungeonContinueCost(ushort value)
        => value > 0
            && (Array.IndexOf(NormalDungeonContinueCosts, value) >= 0
                || Array.IndexOf(SpecialDungeonContinueCosts, value) >= 0);
    private const int ArenaResettingResponsePayloadLength = 40;
    private static readonly TimeSpan ArenaResultCollectionTimeout = TimeSpan.FromSeconds(5);
    private const int DungeonStageRecordsPayloadLength = 4;
    private const byte DungeonEpisodeCount = 20;
    private const byte DungeonCountPerEpisode = 3;
    private const byte DungeonDifficultyCount = 3;
    private const int C355FrameLength = 0x2D8;
    private const int NativeHeaderLength = 8;
    private const int C355DungeonClearFrameOffset = 0x3C;
    private const int C355DungeonClearLength = DungeonEpisodeCount * DungeonDifficultyCount;
    private const int C355VillagePrerequisiteFrameOffset = 0x80;
    private const int C355VillagePrerequisiteLength = sizeof(ulong);
    private const ulong C355VillagePrerequisiteLow44Mask = (1UL << 44) - 1UL;
    private const int C355DungeonRatingsFrameOffset = 0x88;
    private const int C355DungeonRatingsLength = DungeonEpisodeCount * DungeonDifficultyCount;
    private const int C355SecretRatingsFrameOffset = 0xC4;
    private const int C355SecretRatingsLength = 4;
    private const int C355FrontierRatingsFrameOffset = 0xC8;
    private const int C355FrontierRatingsLength = 23;
    private const int C355PartnerNameFrameOffset = 0xDF;
    private const int C355PartnerNameLength = 17;
    private const int C355RingSuffixFrameOffset = 0xF0;
    private const int C355RevivalCountFrameOffset = 0xF2;
    private const uint CoupleRingCodeBase = 43_000_000u;
    private const string CoupleRingCatalogSource = "CI._D28/COUPLERING";
    private const byte DungeonLevelFallbackEpisodeCount = 16;
    // The stage-record UI only has qz_inter_lv_icon1..7. Values above seven
    // make its resource loader enter the CRT invalid-parameter path.
    private const int ClientMaximumLevelIcon = 7;
    private const int DungeonPeerHeartbeatPayloadLength = 8;
    private const int DungeonPeerStatePayloadLength = 68;
    private const int DungeonPeerEntityEventPayloadLength = 8;
    private const int DungeonPeerTriggerPayloadLength = 4;
    private const int BoxInfoResponsePayloadLength = 0x144 - 8;
    private const int DdakgiGuideStepPayloadLength = 4;
    private static readonly TimeSpan DdakgiGuideStepResponseDelay = TimeSpan.FromMilliseconds(50);
    private const uint PermanentItemExpiration = 2_100_123_100;
    private const int ShopMoveRequestPayloadLength = 4;
    private const int VillageShopEnterRequestPayloadLength = 4;
    private const int ShopUserInfoRequestPayloadLength = 4;
    private const int ShopUserInfoResponsePayloadLength = 100;
    private const int PetChargeListRequestPayloadLength = 0;
    private const int PetChargeListResponsePayloadLength = 2032;
    private const int PetChargeRequestPayloadLength = 36;
    private const int PetChargeResponsePayloadLength = 4;
    private const int PetChargeCapacity = 56;
    private const int ShopPurchaseRequestPayloadLength = 8;
    private const int ShopPurchaseResponsePayloadLength = 96;
    private const int NanaPurchaseRequestPayloadLength = 60;
    private const int NanaPurchaseResponsePayloadLength = 24;
    private const int NanaPurchaseItemCountOffset = 6;
    private const int NanaPurchaseItemOptionOffset = 7;
    private const int NanaPurchaseItemCodeOffset = 20;
    private const int NanaPurchaseCapacity = 10;
    private const int NanaWishlistRequestPayloadLength = 0;
    private const int NanaWishlistChoiceRequestPayloadLength = 4;
    private const int NanaWishlistDeleteRequestPayloadLength = 12;
    private const int NanaWishlistRecordLength = 12;
    private const int NanaWishlistCapacity = 8;
    private const int InteriorPurchaseRequestPayloadLength = 208;
    private const int InteriorPurchaseResponsePayloadLength = 1032;
    private const int InteriorPurchaseRequestCapacity = 40;
    private const int InteriorPurchaseRecordLength = 12;
    private const int SpecialTokenPurchaseRequestPayloadLength = 8;
    private const int SpecialCardPurchaseRequestPayloadLength = 8;
    private const int SpecialCardPurchaseResponsePayloadLength = 4;
    private const int ShopGiftRequestPayloadLength = 76;
    private const int ShopGiftResponsePayloadLength = 24;
    private const int ShopGiftRecipientOffset = 8;
    private const int ShopGiftRecipientLength = 16;
    private const int ProfileQueryPayloadLength = 20;
    private const int ProfileQueryEntityIdOffset = 2;
    private const int ProfileQueryIdentityOffset = 4;
    private const int ProfileQueryIdentityLength = 16;
    private const int SceneTransitionRequestPayloadLength = 4;
    private const int SceneTransitionResponsePayloadLength = 8;
    private const int MiniRoomMoveRequestPayloadLength = 20;
    private const int MiniRoomMoveIdentityOffset = 4;
    private const int MiniRoomMoveIdentityLength = 16;
    private const int MiniRoomMoveResponsePayloadLength = 104;
    private const int MiniRoomUserInfoRequestPayloadLength = 4;
    private const int MiniRoomUserInfoResponsePayloadLength = 116;
    private const int MiniRoomObjectInfoRequestPayloadLength = 4;
    private const int ApartmentInteriorInfoRequestPayloadLength = 4;
    private const int ApartmentInteriorInfoResponsePayloadLength = 1036;
    private const int ApartmentInteriorObjectCapacity = 84;
    private const int ApartmentInteriorObjectRecordLength = 12;
    private const int MiniRoomObjectInfoResponsePayloadLength =
        4 + ApartmentInteriorObjectCapacity * ApartmentInteriorObjectRecordLength;
    private const ushort MiniRoomObjectInfoFinalSnapshot = 2000;
    private const int InteriorCatalogRequestPayloadLength = 4;
    private const int InteriorCatalogResponsePayloadLength = 44;
    private const int InteriorCatalogCapacity = 8;
    private const int ApartmentRecommendCountRequestPayloadLength = 0;
    private const int InteriorWishlistRequestPayloadLength = 0;
    private const int InteriorWishlistChoiceRequestPayloadLength = 4;
    private const int InteriorWishlistDeleteRequestPayloadLength = 12;
    private const int InteriorWishlistRecordLength = 12;
    private const int InteriorWishlistCapacity = 40;
    private const int ShopWishlistRequestPayloadLength = 0;
    private const int ShopWishlistChoiceRequestPayloadLength = 4;
    private const int ShopWishlistDeleteRequestPayloadLength = 8;
    private const int ShopWishlistRecordLength = 8;
    private const int ShopWishlistCapacity = 8;
    private const int ShopOwnedStateRequestPayloadLength = 4;
    private const int ShopOwnedStateCapacity = 100;
    private const int InteriorChangeRequestPayloadLength = 760;
    private const int InteriorChangeTakeOnRecordLength = 8;
    private const int InteriorChangeTakeOffOffset = 672;
    private const int InteriorChangeTakeOnCountOffset = 756;
    private const int InteriorChangeTakeOffCountOffset = 758;
    private const int NativeTransportMaximumSequence = 100;
    private const int ListenerBacklog = 2048;
    private const int SocketBufferSize = 256 * 1024;
    private const int OutboundQueueCapacity = 2048;
    private const int OutboundHighFrequencyLimit = 1536;
    private static readonly byte[] NativeTransportTags =
    [
        0x0E, 0x10, 0x12, 0x18, 0x1A, 0x1C, 0x14, 0x0C, 0x16, 0x0A,
        0x11, 0x13, 0x0B, 0x0F, 0x15, 0x0D, 0x0D, 0x09, 0x19, 0x1B,
    ];

    private readonly DatabaseService _database;
    private readonly Action<string> _log;
    private readonly LoginAuthProtocol _loginAuth;
    private readonly List<(TcpListener Listener, string Channel, int Port)> _listeners = [];
    // The login socket closes before the world socket is opened. Keep the
    // short-lived handoff ticket separate from durable world presence.
    private readonly ConcurrentDictionary<string, LoginTicket> _loginTicketsByUsername = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LoginTicket> _loginTicketsByCharacterName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, LoginTicket> _loginTicketsByAccountId = new();
    private readonly ConcurrentDictionary<string, WorldPresence> _activeWorldSessions = new();
    private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
    private readonly ConcurrentDictionary<uint, LaunchTicket> _pendingLaunchTickets = new();
    private readonly ConcurrentDictionary<string, DateTime> _seenAuthNonces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AuthFailureState> _authFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, DungeonRoom> _dungeonRooms = [];
    private readonly object _dungeonRoomGate = new();
    private readonly Dictionary<int, ArenaRoom> _arenaRooms = [];
    private readonly ConcurrentDictionary<string, ConnectionSession> _activeArenaSessions = new(StringComparer.Ordinal);
    private readonly object _arenaRoomGate = new();
    private readonly Dictionary<int, TradeRoom> _tradeRooms = [];
    private readonly Dictionary<string, TradeInvitation> _tradeInvitationsByInvitee = new(StringComparer.Ordinal);
    private readonly object _tradeRoomGate = new();
    private readonly Dictionary<int, Party> _parties = [];
    private readonly Dictionary<string, PartyInvitation> _partyInvitationsByInvitee = new(StringComparer.Ordinal);
    private readonly object _partyGate = new();
    private readonly ConcurrentDictionary<long, byte> _mentorAdvertisingCharacters = new();
    private readonly Dictionary<MentorPendingKey, MentorPendingRequest> _mentorPendingRequests = [];
    private readonly object _mentorGate = new();
    private readonly Dictionary<CouplePendingKey, CouplePendingRequest> _couplePendingRequests = [];
    private readonly object _coupleGate = new();
    private readonly SemaphoreSlim _presenceGate = new(1, 1);
    private readonly object _villageBotGate = new();
    private readonly SemaphoreSlim _villageBotReconcileGate = new(1, 1);
    private readonly Dictionary<TownInstanceKey, List<VillageBot>> _villageBotGroups = [];
    private readonly Dictionary<TownInstanceKey, Queue<string>> _villageBotRecentMessages = [];
    private CancellationTokenSource? _cts;
    private Task? _villageBotTask;
    private VillageBotSettings _villageBotSettings = new();
    private AccountLoginPolicy _loginPolicy = AccountLoginPolicy.Disabled;
    private List<AdapterEndpoint> _endpoints = [];
    private long _nextClientTaskId;
    private int _nextDungeonRoomId;
    private int _nextArenaRoomId;
    private int _nextTradeRoomId;
    private int _nextPartyId;

    private sealed class ConnectionSession
    {
        public long AccountId { get; set; }
        public string Username { get; set; } = string.Empty;
        public CharacterRecord? Character { get; set; }
        public int ChannelId { get; set; }
        public int ListenerPort { get; set; }
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public string? RemoteIp { get; set; }
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
        public CancellationTokenSource? ConnectionCancellation { get; set; }
        public NetworkStream? Stream { get; set; }
        public Channel<OutboundNativeWrite> OutboundWrites { get; } = Channel.CreateBounded<OutboundNativeWrite>(
            new BoundedChannelOptions(OutboundQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        public Task? OutboundWriterTask { get; set; }
        public List<PendingNativeBroadcast> PendingBroadcasts { get; } = [];
        public List<PendingSessionBroadcast> PendingSessionBroadcasts { get; } = [];
        public string? DisconnectReason { get; set; }
        public byte ClientTransportXorKey { get; set; } = 0x17;
        public byte ResponseTransportXorKey { get; set; } = 0x0E;
        public bool ResponseTransportInitialized { get; set; }
        public byte ResponseTransportSequence { get; set; }
        public byte ResponseTransportTagIndex { get; set; }
        public bool ResponseTransportTagInitialized { get; set; }
        public ushort? LastSkillSlotExpansionRequestControl { get; set; }
        public DateTime LastSkillSlotExpansionRequestUtc { get; set; }
        public byte[]? LastSkillSlotExpansionResultPayload { get; set; }
        public Dictionary<uint, uint> MikeItemSelectors { get; } = [];
        public ushort LastReportedPositionX { get; set; }
        public ushort LastReportedPositionY { get; set; }
        public bool HasReportedDungeonPosition { get; set; }
        public bool OnlineTracked { get; set; }
        public byte TownId { get; set; }
        public byte TownPage { get; set; }
        public bool TownSceneActive { get; set; }
        public bool TownMapMarkerInitialized { get; set; }
        public long ApartmentOwnerCharacterId { get; set; }
        public byte VillageShopCode { get; set; }
        public int TradeRoomId { get; set; }
        public int PartyId { get; set; }
        public byte[] DungeonSlotStates { get; } = [1, 1, 1];
        public int DungeonRoomId { get; set; }
        public byte DungeonSlotIndex { get; set; }
        public bool DungeonReady { get; set; }
        public byte DungeonTeamCode { get; set; }
        public bool DungeonMulticastInitialized { get; set; }
        public string? P2PIpAddress { get; set; }
        public ushort P2PPort { get; set; }
        public bool P2PInfoRegistered { get; set; }
        public bool AuxiliaryGameSession { get; set; }
        public byte ArenaGameType { get; set; }
        public int ArenaRoomId { get; set; }
        public byte ArenaSlotIndex { get; set; }
        public bool ArenaReady { get; set; }
        public byte ArenaTeamCode { get; set; }
        public bool ArenaMulticastInitialized { get; set; }
        public bool ArenaP2PProtocolConfirmed { get; set; }
        public int EntertainmentRoomId { get; set; }
        public byte EntertainmentSlotIndex { get; set; }
        public bool EntertainmentReady { get; set; }
        public byte EntertainmentTeamCode { get; set; }
        public bool EntertainmentWaitingRoomInitialized { get; set; }
        public bool EntertainmentMulticastInitialized { get; set; }
        public bool EntertainmentP2PProtocolConfirmed { get; set; }
        public NativeDungeonClient? NativeDungeon { get; set; }
        public NativeDungeonPool.Lease? NativeLease { get; set; }
        public NativeDungeonState? NativeCheckpoint { get; set; }
        public BattleResourceSnapshot? PendingBattleResourceSnapshot { get; set; }
        public byte? NativeBattleAttackMode { get; set; }
        public bool NativeForwarding { get; set; }
        public bool NativeDungeonDeathLatched { get; set; }
        public bool NativeDungeonSettlementAwaitingAction { get; set; }
        public bool NativeDungeonSelectionValid { get; set; }
        public byte NativeDungeonHdIndex { get; set; }
        public byte NativeDungeonEpisode { get; set; }
        public byte NativeDungeonDungeon { get; set; }
        public byte NativeDungeonStage { get; set; }
        public byte NativeDungeonLogicalDifficulty { get; set; }
    }

    private sealed record PendingNativeBroadcast(
        WorldPresence Target,
        ushort Opcode,
        byte[] Payload,
        string Reason);

    private sealed record PendingSessionBroadcast(
        ConnectionSession Target,
        ushort Opcode,
        byte[] Payload,
        string Reason);

    private sealed record OutboundNativeWrite(
        byte[] Frames,
        string Channel,
        int Port,
        string Remote,
        bool UseTransportSequence,
        bool IsBroadcast,
        string? Reason);

    private readonly record struct TownInstanceKey(int ChannelId, byte TownId, byte TownPage);

    private sealed class VillageBot
    {
        public required CharacterRecord Character { get; init; }
        public required ushort CharacterUid { get; set; }
        public ushort EntityId { get; set; }
        public TownInstanceKey? Instance { get; set; }
        public ushort TargetX { get; set; }
        public ushort TargetY { get; set; }
        public ushort HomeX { get; set; }
        public ushort HomeY { get; set; }
        public int PauseTicks { get; set; }
        public long NextMoveAtTick { get; set; }
        public byte Direction { get; set; } = 4;
        public byte LastDirection { get; set; } = 4;
        public VillageBotPortal? PendingPortal { get; set; }
        public long NextSpeechAtTick { get; set; }
        public long NextEmotionAtTick { get; set; }
        public long NextReplyAtTick { get; set; }
        public long LastTransitionAtTick { get; set; }
    }

    private sealed record PartyLeaveNotification(
        WorldPresence Target,
        byte[] Payload);

    private readonly record struct MentorPendingKey(
        string ResponderSessionId,
        ushort ResponseOpcode,
        string RequesterName);

    private sealed record MentorPendingRequest(
        long InteractionId,
        ushort RequestOpcode,
        WorldPresence Requester,
        WorldPresence Responder,
        byte[] RequestPayload,
        DateTime CreatedAtUtc);

    private readonly record struct CouplePendingKey(
        string ResponderSessionId,
        ushort ResponseOpcode,
        string RequesterName);

    private sealed record CouplePendingRequest(
        ushort RequestOpcode,
        WorldPresence Requester,
        WorldPresence Responder,
        uint ItemCode,
        ushort InventorySlot,
        byte[] RequestPayload,
        DateTime CreatedAtUtc);

    private enum DungeonBattleState : byte
    {
        Prepared,
        Active,
        Settled
    }

    private enum DungeonSettlementAction : byte
    {
        None,
        RetryCurrent,
        NextDungeon,
        ChallengeBoss,
        NextDifficulty,
        ReturnVillage
    }

    private enum DungeonTransitionReentryTrigger : byte
    {
        TownPage,
        GameConnection
    }

    private readonly record struct DungeonNpcKey(uint RuntimeUid, byte TargetIndex);

    private sealed class DungeonNpcState(DungeonCombatTemplate template)
    {
        public DungeonCombatTemplate Template { get; } = template;
        public int CurrentHp { get; set; } = template.Hp;
        public bool Defeated => CurrentHp <= 0;
    }

    private sealed class DungeonBossComponentState(DungeonCombatTemplate template)
    {
        public DungeonCombatTemplate Template { get; } = template;
        public int CurrentHp { get; set; } = template.Hp;
        public bool Defeated => CurrentHp <= 0;
        public bool ResolutionAnnounced { get; set; }
    }

    private sealed class DungeonBossState(DungeonBossTemplate template)
    {
        public DungeonBossTemplate Template { get; } = template;
        public Dictionary<DungeonBossComponentKey, DungeonBossComponentState> Components { get; } =
            template.Components.ToDictionary(
                pair => pair.Key,
                pair => new DungeonBossComponentState(pair.Value));
        public int CurrentEnergy => Components.Values.Sum(component => component.CurrentHp);
        public bool Defeated => CurrentEnergy == 0;
        public bool ClearAnnounced { get; set; }
    }

    private sealed class DungeonActiveSkillState
    {
        public required uint SkillCode { get; init; }
        public required byte Grade { get; init; }
        public required ushort AttackValue { get; init; }
        public required ushort ActiveFrames { get; init; }
        public required bool CreatesIndependentAttack { get; init; }
        public required long ActiveUntilTick { get; set; }
    }

    private sealed class DungeonBattleInstance
    {
        public DungeonBattleInstance(
            byte hdIndex,
            byte episode,
            byte dungeon,
            byte stage,
            byte logicalDifficulty = 0)
        {
            HdIndex = hdIndex;
            Episode = episode;
            Dungeon = dungeon;
            Stage = stage;
            LogicalDifficulty = logicalDifficulty;
        }

        public byte HdIndex { get; }
        public byte Episode { get; set; }
        public byte Dungeon { get; set; }
        public byte Stage { get; set; }
        public byte LogicalDifficulty { get; set; }
        public ushort RequestedStageIndex { get; set; }
        public ushort ShowStageNumber { get; set; }
        public DungeonBattleState State { get; set; } = DungeonBattleState.Prepared;
        public DateTime PreparedUtc { get; } = DateTime.UtcNow;
        public DateTime? StartedUtc { get; set; }
        public int PartySizeAtStart { get; set; }
        public HashSet<long> ParticipantCharacterIds { get; } = [];
        public Dictionary<long, int> HitScores { get; } = [];
        public Dictionary<long, int> BossBonusScores { get; } = [];
        public Dictionary<long, int> BossHansRewards { get; } = [];
        public Dictionary<DungeonNpcKey, DungeonNpcState> Npcs { get; } = [];
        public HashSet<DungeonNpcKey> DefeatedUncataloguedRuntimeUids { get; } = [];
        public Dictionary<ushort, DungeonBossState> Bosses { get; } = [];
        public HashSet<long> RewardedCharacters { get; } = [];
        public HashSet<long> RewardCommittingCharacters { get; } = [];
        public Dictionary<long, byte[]> EndGamePayloads { get; } = [];
        public bool SettlementStarted { get; set; }
        public HashSet<long> DeadCharacters { get; } = [];
        public Dictionary<long, byte> RemainingContinues { get; } = [];
        public HashSet<long> ContinuingCharacters { get; } = [];
        public Dictionary<long, DungeonActiveSkillState> ActiveSkills { get; } = [];
        public Dictionary<(long CharacterId, uint SkillCode), long> SkillCooldowns { get; } = [];
        public HashSet<(ushort PickupType, ushort DropUid)> ClaimedDrops { get; } = [];
        public Dictionary<ushort, uint> GeneratedDrops { get; } = [];
        public byte[]? UpgradeDropSubtypes { get; set; }
        public byte[]? InDungeonItemDropCodes { get; set; }
        public byte[]? RandomPercentPlanA { get; set; }
        public byte[]? RandomPercentPlanB { get; set; }
        public Dictionary<string, byte[]> GameDataPayloadsBySession { get; } = new(StringComparer.Ordinal);
        public ushort? SelectedMapIndex { get; set; }
        public HashSet<string> EntityInitializedSessions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EntityAnnouncements { get; } = new(StringComparer.Ordinal);
        public ushort? LastBossResourceUid { get; set; }
        public ushort NextGeneratedDropToken { get; set; } = 1;
    }

    private sealed class DungeonRoom
    {
        public required int Id { get; init; }
        public required int ChannelId { get; init; }
        public required string OwnerSessionId { get; set; }
        public byte[] CreateRequestPayload { get; init; } = new byte[44];
        public Dictionary<string, DungeonMember> Members { get; } = new(StringComparer.Ordinal);
        public byte[] SlotStates { get; } = [1, 1, 1];
        public DungeonBattleInstance Battle { get; set; } = new(0, 0, 0, 0);
        public HashSet<string> TransitioningSessionIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> TransitionDisconnectedSessionIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> TransitionGameConnectedSessionIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> TransitionReenteredSessionIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> TransitionTownReentrySentSessionIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> TransitionConnectionReentrySentSessionIds { get; } = new(StringComparer.Ordinal);
        public DateTime? TransitionStartedUtc { get; set; }
        public DungeonSettlementAction SettlementAction { get; set; }

        public byte PendingEpisode { get; set; }
        public byte PendingDungeon { get; set; }
        public byte PendingStage { get; set; }
        public byte PendingLogicalDifficulty { get; set; }
        public bool HasPendingTransition { get; set; }

        public byte Episode => CreateRequestPayload.Length > 27 ? CreateRequestPayload[27] : (byte)0;
        public byte Dungeon => CreateRequestPayload.Length > 28 ? CreateRequestPayload[28] : (byte)0;
        public byte Stage => CreateRequestPayload.Length > 29 ? CreateRequestPayload[29] : (byte)0;
        public byte Difficulty => CreateRequestPayload.Length >= 32
            ? checked((byte)BinaryPrimitives.ReadUInt16LittleEndian(CreateRequestPayload.AsSpan(30, 2)))
            : (byte)0;
        public byte HdIndex => CreateRequestPayload.Length > 26 ? CreateRequestPayload[26] : (byte)0;

        // Compatibility accessors keep the protocol handlers narrow while the
        // room and battle lifetimes remain physically separate.
        public Dictionary<long, int> HitScores => Battle.HitScores;
        public Dictionary<long, int> BossBonusScores => Battle.BossBonusScores;
        public Dictionary<long, int> BossHansRewards => Battle.BossHansRewards;
        public Dictionary<DungeonNpcKey, DungeonNpcState> Npcs => Battle.Npcs;
        public Dictionary<ushort, DungeonBossState> Bosses => Battle.Bosses;
        public HashSet<long> RewardedCharacters => Battle.RewardedCharacters;
        public Dictionary<long, byte[]> EndGamePayloads => Battle.EndGamePayloads;
        public HashSet<(ushort PickupType, ushort DropUid)> ClaimedDrops => Battle.ClaimedDrops;
        public Dictionary<ushort, uint> GeneratedDrops => Battle.GeneratedDrops;
        public byte[]? UpgradeDropSubtypes
        {
            get => Battle.UpgradeDropSubtypes;
            set => Battle.UpgradeDropSubtypes = value;
        }
        public byte[]? InDungeonItemDropCodes
        {
            get => Battle.InDungeonItemDropCodes;
            set => Battle.InDungeonItemDropCodes = value;
        }
        public Dictionary<string, byte[]> GameDataPayloadsBySession => Battle.GameDataPayloadsBySession;
        public HashSet<string> EntityInitializedSessions => Battle.EntityInitializedSessions;
        public HashSet<string> EntityAnnouncements => Battle.EntityAnnouncements;
        public ushort? SelectedMapIndex
        {
            get => Battle.SelectedMapIndex;
            set => Battle.SelectedMapIndex = value;
        }
        public uint BossEnergy => Battle.LastBossResourceUid is { } uid
            && Battle.Bosses.TryGetValue(uid, out var boss)
                ? checked((uint)boss.CurrentEnergy)
                : 0;
        public bool BossClearAnnounced => Battle.Bosses.Values.Any(boss => boss.ClearAnnounced);
        public bool Started
        {
            get => Battle.State == DungeonBattleState.Active;
            set
            {
                Battle.State = value ? DungeonBattleState.Active : DungeonBattleState.Prepared;
                Battle.StartedUtc = value ? Battle.StartedUtc ?? DateTime.UtcNow : null;
            }
        }
        public byte BattleEpisode
        {
            get => Battle.Episode;
            set => Battle.Episode = value;
        }
        public byte BattleDungeon
        {
            get => Battle.Dungeon;
            set => Battle.Dungeon = value;
        }
        public byte BattleStage
        {
            get => Battle.Stage;
            set => Battle.Stage = value;
        }
        public byte BattleLogicalDifficulty
        {
            get => Battle.LogicalDifficulty;
            set => Battle.LogicalDifficulty = value;
        }
    }

    private sealed class ArenaRoom
    {
        public required int Id { get; init; }
        public required int ChannelId { get; init; }
        public required byte GameType { get; init; }
        public required string OwnerSessionId { get; set; }
        public required ArenaCreateRequest CreateRequest { get; init; }
        public Dictionary<string, ConnectionSession> Members { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EntityInitializedSessions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EntityAnnouncements { get; } = new(StringComparer.Ordinal);
        // CF6D/CF78 consumers treat 2 as unavailable and every other value as
        // available. Retail initializes the three guest positions to 1.
        public byte[] SlotStates { get; } = [1, 1, 1];
        public Dictionary<string, uint> EndValuesBySession { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EndingSessionIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ResultSessionIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ResetSessionIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ushort> CurrentHpBySession { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ScoreBySession { get; } = new(StringComparer.Ordinal);
        public HashSet<uint> ClearedEntityRuntimeUids { get; } = [];
        public HashSet<(ushort PickupType, ushort DropUid)> ClaimedDrops { get; } = [];
        public byte[] PvpResultPayload { get; set; } = [];
        public ushort SelectedMode { get; set; }
        public ushort SelectedMap { get; set; }
        public byte[] GameDataPayload { get; set; } = [];
        public bool Started { get; set; }
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;

        public string Title => CreateRequest.Title;
        public string Password => CreateRequest.Password;
        public uint Property => CreateRequest.Metadata;
        public byte ResponseField2 => CreateRequest.ResponseField2;
        public byte ResponseField3 => CreateRequest.ResponseField3;
    }

    private sealed record DungeonMember(ConnectionSession Session, byte SlotIndex);

    private sealed class TradeRoom
    {
        public required int Id { get; init; }
        public required int ChannelId { get; init; }
        public required ConnectionSession Inviter { get; init; }
        public required ConnectionSession Invitee { get; init; }
        public HashSet<string> JoinedSessionIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TradeOffer> Offers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ReadySessionIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FinalSessionIds { get; } = new(StringComparer.Ordinal);
        public bool Settling { get; set; }
    }

    private sealed class TradeOffer
    {
        public TradeCardOffer?[] Cards { get; } = new TradeCardOffer?[TradeCardSlotCount];
        public ulong Hans { get; set; }

        public TradeOffer Clone()
        {
            var copy = new TradeOffer { Hans = Hans };
            Cards.CopyTo(copy.Cards, 0);
            return copy;
        }
    }

    private readonly record struct TradeCardOffer(
        byte Chapter,
        byte Page,
        byte Index,
        byte Count,
        uint ItemCode);

    private sealed record TradeSettlementSnapshot(
        int RoomId,
        ConnectionSession First,
        TradeOffer FirstOffer,
        ConnectionSession Second,
        TradeOffer SecondOffer);

    private sealed class TradeInvitation
    {
        public required ConnectionSession Inviter { get; init; }
        public required ConnectionSession Invitee { get; init; }
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public bool Accepted { get; set; }
    }

    private sealed class Party
    {
        public required int Id { get; init; }
        public required int ChannelId { get; init; }
        public required string OwnerSessionId { get; set; }
        public byte[] OwnerMetadata { get; set; } = new byte[4];
        public Dictionary<string, PartyMember> Members { get; } = new(StringComparer.Ordinal);
        public long NextJoinOrder { get; set; }
    }

    private sealed record PartyMember(ConnectionSession Session, long JoinOrder);

    private sealed class PartyInvitation
    {
        public required ConnectionSession Inviter { get; init; }
        public required ConnectionSession Invitee { get; init; }
        public byte[] InviterMetadata { get; init; } = new byte[4];
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    }

    private sealed record P2PPeerEndpoint(
        byte SlotIndex,
        ushort CharacterUid,
        string IpAddress,
        ushort Port);

    private sealed record LoginTicket(
        long AccountId,
        string Username,
        string? CharacterName,
        string? RemoteIp,
        DateTime ExpiresAtUtc,
        bool ChannelReentry);

    private sealed record LaunchTicket(
        long AccountId,
        string Username,
        string? RemoteIp,
        DateTime ExpiresAtUtc);

    private sealed class AuthFailureState
    {
        public object Gate { get; } = new();
        public DateTime WindowStartedUtc { get; set; } = DateTime.UtcNow;
        public int Count { get; set; }
        public DateTime BlockedUntilUtc { get; set; }
    }

    private sealed class WorldPresence
    {
        private long _lastHeartbeatUtcTicks;
        private long _heartbeatCount = 1;

        public WorldPresence(
            ConnectionSession session,
            string sessionId,
            long accountId,
            long characterId,
            string username,
            string characterName,
            string? remoteIp,
            int channelId,
            DateTime onlineSinceUtc,
            DateTime lastHeartbeatUtc,
            Action<string> disconnect)
        {
            Session = session;
            SessionId = sessionId;
            AccountId = accountId;
            CharacterId = characterId;
            Username = username;
            CharacterName = characterName;
            RemoteIp = remoteIp ?? "unknown";
            ChannelId = channelId;
            OnlineSinceUtc = onlineSinceUtc;
            _lastHeartbeatUtcTicks = lastHeartbeatUtc.Ticks;
            Disconnect = disconnect;
        }

        public ConnectionSession Session { get; }
        public string SessionId { get; }
        public long AccountId { get; }
        public long CharacterId { get; }
        public string Username { get; }
        public string CharacterName { get; }
        public string RemoteIp { get; }
        public int ChannelId { get; }
        public DateTime OnlineSinceUtc { get; }
        private Action<string> Disconnect { get; }
        public DateTime LastHeartbeatUtc => new(
            Interlocked.Read(ref _lastHeartbeatUtcTicks),
            DateTimeKind.Utc);
        public long HeartbeatCount => Interlocked.Read(ref _heartbeatCount);

        public void Touch(DateTime utcNow)
        {
            Interlocked.Exchange(ref _lastHeartbeatUtcTicks, utcNow.Ticks);
            Interlocked.Increment(ref _heartbeatCount);
        }

        public void RequestDisconnect(string reason) => Disconnect(reason);
    }

    public NetworkAdapterService(DatabaseService database, Action<string> log, string configurationDirectory)
    {
        _database = database;
        _log = message =>
        {
            if (!IsHighFrequencyLog(message))
                log(message);
        };
        _loginAuth = new LoginAuthProtocol(Path.Combine(configurationDirectory, "login-auth-key.pk8"));
    }

    private static bool IsHighFrequencyLog(string message) =>
        message.Contains("opcode=0x044C", StringComparison.Ordinal)
        || message.Contains("opcode=0x0578", StringComparison.Ordinal)
        || message.Contains("opcode=0x03E8", StringComparison.Ordinal)
        || message.Contains("opcode=0xD00D", StringComparison.Ordinal)
        || message.Contains("opcode=0xD00E", StringComparison.Ordinal)
        || message.Contains("opcode=0xCB21", StringComparison.Ordinal)
        || message.Contains("Dungeon projectile collision acknowledged", StringComparison.Ordinal)
        || message.Contains("dungeon shooting state", StringComparison.Ordinal)
        || message.Contains("dungeon peer loading heartbeat", StringComparison.Ordinal)
        || message.Contains("dungeon peer player state", StringComparison.Ordinal)
        || message.Contains("鍦板灏勫嚮鐘舵€佸凡鍚屾", StringComparison.Ordinal)
        || message.Contains("scene movement/state", StringComparison.Ordinal);

    private static bool IsHighFrequencyOpcode(ushort opcode) =>
        opcode is 0x044C or 0x0578 or 0x03E8 or 0xD00D or 0xD00E or 0xCB21;

    public bool IsRunning => _cts is not null;
    public IReadOnlyList<AdapterEndpoint> Endpoints => _endpoints;
    public event Action? AccountStateChanged;
    public event Action? MentorStateChanged;
    public event Action? FriendStateChanged;

    public VillageBotRuntimeStatus GetVillageBotStatus()
    {
        var activeKeys = _activeWorldSessions.Values
            .Where(item => item.Session.OnlineTracked && item.Session.TownSceneActive)
            .Select(item => new TownInstanceKey(item.Session.ChannelId, item.Session.TownId, item.Session.TownPage))
            .ToHashSet();
        lock (_villageBotGate)
        {
            var activeBots = _villageBotGroups
                .Where(pair => activeKeys.Contains(pair.Key))
                .SelectMany(pair => pair.Value)
                .ToArray();
            return new VillageBotRuntimeStatus
            {
                Enabled = _villageBotSettings.Enabled,
                ServiceRunning = IsRunning,
                ConfiguredCount = _villageBotSettings.Count,
                ActiveCount = activeBots.Length,
                SpeechEnabled = _villageBotSettings.SpeechEnabled,
                ConversationEnabled = _villageBotSettings.ConversationEnabled,
                EmotionEnabled = _villageBotSettings.EmotionEnabled,
                MovementEnabled = _villageBotSettings.MovementEnabled,
                PauseEnabled = _villageBotSettings.PauseEnabled,
                PortalTravelEnabled = _villageBotSettings.PortalTravelEnabled,
                Bots = activeBots.Select(item => new VillageBotRuntimeRecord
                {
                    Name = item.Character.Name,
                    EntityId = item.EntityId,
                    ChannelId = item.Instance?.ChannelId ?? 0,
                    TownId = item.Instance?.TownId ?? 0,
                    TownPage = item.Instance?.TownPage ?? 0,
                    PositionX = (ushort)Math.Clamp(item.Character.PositionX, 0, ushort.MaxValue),
                    PositionY = (ushort)Math.Clamp(item.Character.PositionY, 0, ushort.MaxValue),
                    State = !_villageBotSettings.MovementEnabled
                        ? "停止行走"
                        : item.PendingPortal is { } portal
                        ? $"前往页面 {portal.DestinationPage}"
                        : item.PauseTicks > 0 ? "停留" : "行走",
                    GenderName = item.Character.Gender == 1 ? "男" : "女",
                    Level = item.Character.Level,
                    PetItemCode = GetEquippedPetItemCode(item.Character),
                    PetName = GetVillageBotPetName(item.Character),
                    OutfitName = GetVillageBotOutfitName(item.Character),
                    AppearanceHex = Convert.ToHexString(BuildStoredAppearance(item.Character))
                }).ToList()
            };
        }
    }

    public async Task ApplyVillageBotSettingsAsync(
        VillageBotSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Count is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(settings.Count), "村庄机器人数量必须在 1-20 之间。");
        if (settings.MinimumSpeechSeconds is < 15 or > 3600
            || settings.MaximumSpeechSeconds is < 15 or > 3600
            || settings.MaximumSpeechSeconds < settings.MinimumSpeechSeconds)
            throw new ArgumentOutOfRangeException(nameof(settings.MinimumSpeechSeconds), "机器人发言间隔必须在 15-3600 秒之间，且最大间隔不能小于最小间隔。");

        lock (_villageBotGate)
        {
            _villageBotSettings = new VillageBotSettings
            {
                Enabled = settings.Enabled,
                Count = settings.Count,
                SpeechEnabled = settings.SpeechEnabled,
                ConversationEnabled = settings.ConversationEnabled,
                EmotionEnabled = settings.EmotionEnabled,
                MovementEnabled = settings.MovementEnabled,
                PauseEnabled = settings.PauseEnabled,
                PortalTravelEnabled = settings.PortalTravelEnabled,
                MinimumSpeechSeconds = settings.MinimumSpeechSeconds,
                MaximumSpeechSeconds = settings.MaximumSpeechSeconds
            };
        }

        if (IsRunning)
            await ReconcileVillageBotsAsync(cancellationToken);
        else
        {
            lock (_villageBotGate)
            {
                _villageBotGroups.Clear();
                _villageBotRecentMessages.Clear();
            }
        }
    }

    public bool IsAccountOnline(long accountId)
        => _activeWorldSessions.Values.Any(item => item.AccountId == accountId);

    public int GetChannelPopulation(int channelId)
        => _activeWorldSessions.Values.Count(item => item.ChannelId == channelId);

    public IReadOnlyList<OnlineConnectionRecord> GetOnlineConnections()
    {
        var snapshotUtc = DateTime.UtcNow;
        return _activeWorldSessions.Values
            .Select(item => new OnlineConnectionRecord
            {
                SessionId = item.SessionId,
                AccountId = item.AccountId,
                Username = item.Username,
                CharacterName = item.CharacterName,
                RemoteIp = item.RemoteIp,
                ChannelId = item.ChannelId,
                OnlineSinceUtc = item.OnlineSinceUtc,
                LastHeartbeatUtc = item.LastHeartbeatUtc,
                HeartbeatCount = item.HeartbeatCount,
                SnapshotUtc = snapshotUtc
            })
            .OrderBy(item => item.OnlineSinceUtc)
            .ToArray();
    }

    public IReadOnlyList<DungeonRoomManagementRecord> GetDungeonRooms()
    {
        lock (_dungeonRoomGate)
        {
            return _dungeonRooms.Values
                .OrderBy(room => room.Id)
                .Select(room =>
                {
                    var owner = room.Members.GetValueOrDefault(room.OwnerSessionId)?.Session.Character;
                    return new DungeonRoomManagementRecord
                    {
                        RoomId = room.Id,
                        ChannelId = room.ChannelId,
                        Title = DecodeDungeonTitle(room),
                        OwnerName = owner?.Name ?? "离线",
                        DungeonStatus = $"{room.Episode + 1}-{room.Dungeon + 1}-{room.Stage + 1}",
                        StateText = room.Started
                            ? room.BossClearAnnounced ? "已通关" : "战斗中"
                            : "等待中",
                        MemberCount = room.Members.Count,
                        BossEnergy = room.BossEnergy
                    };
                })
                .ToArray();
        }
    }

    public IReadOnlyList<DungeonMemberManagementRecord> GetDungeonRoomMembers(int roomId)
    {
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(roomId, out var room))
                return [];
            return room.Members.Values
                .OrderBy(member => member.SlotIndex)
                .Select(member =>
                {
                    var session = member.Session;
                    var character = session.Character;
                    return new DungeonMemberManagementRecord
                    {
                        RoomId = room.Id,
                        SessionId = session.SessionId,
                        AccountId = session.AccountId,
                        Username = session.Username ?? string.Empty,
                        CharacterName = character?.Name ?? "未加载",
                        RoleText = session.SessionId == room.OwnerSessionId ? "房主" : "成员",
                        SlotIndex = member.SlotIndex,
                        ReadyText = session.SessionId == room.OwnerSessionId
                            ? "房主"
                            : session.DungeonReady ? "已准备" : "未准备",
                        TeamCode = session.DungeonTeamCode,
                        HpMpText = character is null
                            ? "-"
                            : $"{character.CurrentHp}/{character.MaxHp}  {character.CurrentMp}/{character.MaxMp}",
                        Score = character is null
                            ? 0
                            : checked(
                                room.HitScores.GetValueOrDefault(character.Id)
                                + room.BossBonusScores.GetValueOrDefault(character.Id)),
                        P2PEndpoint = session.P2PInfoRegistered
                            ? $"{session.P2PIpAddress}:{session.P2PPort}"
                            : "未注册"
                    };
                })
                .ToArray();
        }
    }

    public IReadOnlyList<ArenaRoomManagementRecord> GetArenaRooms()
    {
        lock (_arenaRoomGate)
        {
            return _arenaRooms.Values
                .OrderBy(room => room.Id)
                .Select(room =>
                {
                    var owner = room.Members.GetValueOrDefault(room.OwnerSessionId)?.Character;
                    return new ArenaRoomManagementRecord
                    {
                        RoomId = room.Id,
                        ChannelId = room.ChannelId,
                        GameType = room.GameType,
                        GameTypeText = GetArenaGameTypeText(room.GameType),
                        Title = room.Title,
                        OwnerName = owner?.Name ?? "离线",
                        MemberCount = room.Members.Count,
                        Property = room.Property,
                        CreatedUtc = room.CreatedUtc
                    };
                })
                .ToArray();
        }
    }

    public IReadOnlyList<ArenaMemberManagementRecord> GetArenaRoomMembers(int roomId)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(roomId, out var room))
                return [];
            return room.Members.Values
                .OrderBy(member => member.ArenaSlotIndex)
                .Select(member => new ArenaMemberManagementRecord
                {
                    RoomId = room.Id,
                    SessionId = member.SessionId,
                    AccountId = member.AccountId,
                    Username = member.Username,
                    CharacterName = member.Character?.Name ?? "未加载",
                    RoleText = member.SessionId == room.OwnerSessionId ? "房主" : "成员",
                    SlotIndex = member.ArenaSlotIndex,
                    ReadyText = member.SessionId == room.OwnerSessionId
                        ? "房主"
                        : member.ArenaReady ? "已准备" : "未准备",
                    TeamCode = member.ArenaTeamCode,
                    Level = member.Character?.Level ?? 0,
                    HpMpText = member.Character is null
                        ? "-"
                        : $"{member.Character.CurrentHp}/{member.Character.MaxHp}  {member.Character.CurrentMp}/{member.Character.MaxMp}",
                    P2PEndpoint = member.P2PInfoRegistered
                        ? $"{member.P2PIpAddress}:{member.P2PPort}"
                        : "未注册"
                })
                .ToArray();
        }
    }

    public IReadOnlyList<PartyManagementRecord> GetParties()
    {
        lock (_partyGate)
        {
            return _parties.Values
                .OrderBy(party => party.Id)
                .Select(party => new PartyManagementRecord
                {
                    PartyId = party.Id,
                    ChannelId = party.ChannelId,
                    OwnerName = party.Members.GetValueOrDefault(party.OwnerSessionId)?.Session.Character?.Name ?? "离线",
                    MemberCount = party.Members.Count,
                    StateText = party.Members.Count >= PartyMaximumMembers ? "已满" : "组队中"
                })
                .ToArray();
        }
    }

    public IReadOnlyList<PartyMemberManagementRecord> GetPartyMembers(int partyId)
    {
        lock (_partyGate)
        {
            if (!_parties.TryGetValue(partyId, out var party))
                return [];
            return party.Members.Values
                .OrderBy(member => member.JoinOrder)
                .Select(member =>
                {
                    var session = member.Session;
                    var character = session.Character;
                    return new PartyMemberManagementRecord
                    {
                        PartyId = party.Id,
                        SessionId = session.SessionId,
                        AccountId = session.AccountId,
                        Username = session.Username,
                        CharacterName = character?.Name ?? "未加载",
                        RoleText = session.SessionId == party.OwnerSessionId ? "队长" : "成员",
                        Level = character?.Level ?? 0,
                        JoinOrder = member.JoinOrder,
                        SceneText = character is null
                            ? "-"
                            : $"鍦板浘 {character.CurrentMapId} / 椤甸潰 {character.CurrentTownPage} ({character.PositionX}, {character.PositionY})",
                        OnlineText = session.OnlineTracked ? "在线" : "离线"
                    };
                })
                .ToArray();
        }
    }

    public IReadOnlyList<TradeManagementRecord> GetTradeRooms()
    {
        lock (_tradeRoomGate)
        {
            return _tradeRooms.Values
                .OrderBy(room => room.Id)
                .Select(room => new TradeManagementRecord
                {
                    RoomId = room.Id,
                    ChannelId = room.ChannelId,
                    InviterName = room.Inviter.Character?.Name ?? "未加载",
                    InviteeName = room.Invitee.Character?.Name ?? "未加载",
                    JoinedCount = room.JoinedSessionIds.Count,
                    ReadyCount = room.ReadySessionIds.Count,
                    FinalCount = room.FinalSessionIds.Count,
                    StateText = room.Settling
                        ? "结算中"
                        : room.FinalSessionIds.Count > 0
                            ? "确认中"
                            : room.ReadySessionIds.Count > 0
                                ? "准备中"
                                : room.JoinedSessionIds.Count == 2 ? "交易中" : "等待进入"
                })
                .ToArray();
        }
    }

    public IReadOnlyList<TradeMemberManagementRecord> GetTradeRoomMembers(int roomId)
    {
        lock (_tradeRoomGate)
        {
            if (!_tradeRooms.TryGetValue(roomId, out var room))
                return [];
            return new[] { room.Inviter, room.Invitee }
                .Select(member =>
                {
                    var offer = room.Offers.GetValueOrDefault(member.SessionId);
                    return new TradeMemberManagementRecord
                    {
                        RoomId = room.Id,
                        SessionId = member.SessionId,
                        AccountId = member.AccountId,
                        Username = member.Username,
                        CharacterName = member.Character?.Name ?? "未加载",
                        RoleText = member.SessionId == room.Inviter.SessionId ? "邀请方" : "受邀方",
                        JoinedText = room.JoinedSessionIds.Contains(member.SessionId) ? "已进入" : "未进入",
                        ReadyText = room.ReadySessionIds.Contains(member.SessionId) ? "已准备" : "未准备",
                        FinalText = room.FinalSessionIds.Contains(member.SessionId) ? "已确认" : "未确认",
                        Hans = offer?.Hans ?? 0,
                        CardsText = BuildTradeManagementCardsText(offer)
                    };
                })
                .ToArray();
        }
    }

    private static string BuildTradeManagementCardsText(TradeOffer? offer)
    {
        if (offer is null)
            return "无";
        var cards = offer.Cards
            .Select((card, slot) => (Card: card, Slot: slot))
            .Where(item => item.Card.HasValue)
            .Select(item =>
            {
                var card = item.Card!.Value;
                var name = CardCatalog.TryGet(card.ItemCode, out var catalog) ? catalog.Name : $"#{card.ItemCode}";
                return $"slot {item.Slot + 1} {name} x{card.Count}";
            })
            .ToArray();
        return cards.Length == 0 ? "无" : string.Join("；", cards);
    }

    public bool KickArenaMember(string sessionId, string reason)
    {
        ConnectionSession? target;
        lock (_arenaRoomGate)
            target = _arenaRooms.Values.SelectMany(room => room.Members.Values)
                .FirstOrDefault(member => member.SessionId == sessionId);
        if (target is null)
            return false;
        target.DisconnectReason = string.IsNullOrWhiteSpace(reason) ? "被后台移出天空竞技场" : reason;
        try
        {
            target.ConnectionCancellation?.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public int DisbandArenaRoom(int roomId, string reason)
    {
        ConnectionSession[] members;
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(roomId, out var room))
                return 0;
            members = room.Members.Values.ToArray();
            foreach (var member in members)
                ResetArenaRoomState(member);
            _arenaRooms.Remove(roomId);
        }
        foreach (var member in members)
        {
            member.DisconnectReason = string.IsNullOrWhiteSpace(reason) ? "后台解散天空竞技场房间" : reason;
            try { member.ConnectionCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        return members.Length;
    }

    public int DisbandDungeonRoom(int roomId, string reason)
    {
        string[] sessionIds;
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(roomId, out var room))
                return 0;
            if (room.Battle.ContinuingCharacters.Count != 0
                || room.Battle.RewardCommittingCharacters.Count != 0
                || HasPendingDungeonRewardsLocked(room))
                return 0;
            sessionIds = room.Members.Keys.ToArray();
            foreach (var member in room.Members.Values)
            {
                member.Session.DungeonRoomId = 0;
                member.Session.DungeonSlotIndex = 0;
                member.Session.DungeonReady = false;
                member.Session.DungeonTeamCode = 0;
                member.Session.DungeonMulticastInitialized = false;
                ResetP2PState(member.Session);
            }
            _dungeonRooms.Remove(roomId);
        }
        foreach (var sessionId in sessionIds)
            KickConnection(sessionId, string.IsNullOrWhiteSpace(reason) ? "后台解散地宫房间" : reason);
        return sessionIds.Length;
    }

    public bool ResetDungeonRoomBattle(int roomId)
    {
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(roomId, out var room))
                return false;
            if (room.Battle.ContinuingCharacters.Count != 0
                || room.Battle.RewardCommittingCharacters.Count != 0
                || HasPendingDungeonRewardsLocked(room))
                return false;
            ResetDungeonBattleLocked(room, beginTransition: true);
            foreach (var member in room.Members.Values)
            {
                member.Session.DungeonReady = false;
                member.Session.DungeonTeamCode = 0;
            }
            return true;
        }
    }

    public void ClearAuthenticationBlocks() => _authFailures.Clear();

    public bool KickConnection(string sessionId, string reason)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || !_activeWorldSessions.TryGetValue(sessionId, out var presence))
            return false;
        presence.RequestDisconnect(string.IsNullOrWhiteSpace(reason) ? "后台强制下线" : reason);
        return true;
    }

    public int KickAllConnections(string reason)
    {
        var sessions = _activeWorldSessions.Values.ToArray();
        foreach (var session in sessions)
            session.RequestDisconnect(string.IsNullOrWhiteSpace(reason) ? "后台全部踢下线" : reason);
        return sessions.Length;
    }

    public void ValidateEndpointConfiguration(AdapterOptions options, IEnumerable<AdapterEndpoint> endpoints)
        => _ = CreateEndpointSnapshot(options, endpoints);

    public async Task StartAsync(AdapterOptions options, IEnumerable<AdapterEndpoint> endpoints, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return;
        WireIdentityAllocator.Reset();
        _mentorAdvertisingCharacters.Clear();
        lock (_mentorGate)
            _mentorPendingRequests.Clear();
        await _database.DeactivateAllMentorAdvertisementsAsync(cancellationToken);
        MentorStateChanged?.Invoke();
        var protocolAssembly = typeof(NetworkAdapterService).Assembly;
        var protocolAssemblyPath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "Nanaimo.Adapter.exe");
        var protocolAssemblyTimestamp = File.Exists(protocolAssemblyPath)
            ? File.GetLastWriteTime(protocolAssemblyPath).ToString("yyyy-MM-dd HH:mm:ss.fff")
            : "unknown";
        _log($"协议程序集已加载：file={protocolAssemblyPath} lastWrite={protocolAssemblyTimestamp} mvid={protocolAssembly.ManifestModule.ModuleVersionId:N}");
        var address = IPAddress.TryParse(options.BindAddress, out var parsed) ? parsed : IPAddress.Loopback;
        var endpointSnapshot = CreateEndpointSnapshot(options, endpoints);
        _loginPolicy = AccountLoginPolicy.From(options);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _endpoints = endpointSnapshot;

        var gameStarted = StartTcpListener(address, options.GameAdapterPort, "GameAdapter", 0, _cts.Token);
        var enabledEndpoints = endpointSnapshot.Where(item => item.Enabled).OrderBy(item => item.Id).ToArray();
        var worldStarted = false;
        if (enabledEndpoints.Length == 1)
        {
            worldStarted = StartTcpListener(
                address,
                options.WorldAdapterPort,
                "WorldAdapter",
                enabledEndpoints[0].Id,
                _cts.Token);
        }
        else
        {
            worldStarted = true;
            foreach (var endpoint in enabledEndpoints)
            {
                var endpointAddress = IPAddress.Parse(endpoint.Host);
                var endpointStarted = StartTcpListener(
                    endpointAddress,
                    options.WorldAdapterPort,
                    "WorldAdapter",
                    endpoint.Id,
                    _cts.Token);
                worldStarted &= endpointStarted;
            }
        }
        var arenaStarted = true;
        if (enabledEndpoints.Length == 1)
        {
            for (byte gameType = 1; gameType <= ArenaGameAdapterTypeCount; gameType++)
            {
                arenaStarted &= StartTcpListener(
                    address,
                    GetArenaGameAdapterPort(gameType),
                    "ArenaAdapter",
                    enabledEndpoints[0].Id,
                    _cts.Token);
            }
        }
        else
        {
            foreach (var endpoint in enabledEndpoints)
            {
                var endpointAddress = IPAddress.Parse(endpoint.Host);
                for (byte gameType = 1; gameType <= ArenaGameAdapterTypeCount; gameType++)
                {
                    arenaStarted &= StartTcpListener(
                        endpointAddress,
                        GetArenaGameAdapterPort(gameType),
                        "ArenaAdapter",
                        endpoint.Id,
                        _cts.Token);
                }
            }
        }
        if (!gameStarted || !worldStarted || !arenaStarted)
        {
            await StopAsync();
            throw new InvalidOperationException("登录服务或频道世界服务没有可用的 TCP 监听地址。");
        }
        var villageBotSettings = await _database.GetVillageBotSettingsAsync(cancellationToken);
        lock (_villageBotGate)
            _villageBotSettings = villageBotSettings;
        _villageBotTask = Task.Run(() => VillageBotLoopAsync(_cts.Token), CancellationToken.None);
        await ReconcileVillageBotsAsync(cancellationToken);
        _log($"直连 TCP 服务已启动：GameAdapter={options.GameAdapterPort}，WorldAdapter={options.WorldAdapterPort}");
    }

    private bool StartTcpListener(IPAddress address, int port, string channel, int channelId, CancellationToken token)
    {
        try
        {
            var listener = new TcpListener(address, port);
            listener.Start(ListenerBacklog);
            lock (_listeners) _listeners.Add((listener, channel, port));
            var channelSuffix = channelId > 0 ? $" channelId={channelId}" : string.Empty;
            _log($"{channel} 已监听 {address}:{port}{channelSuffix}");
            _ = AcceptLoopAsync(listener, channel, port, channelId, token);
            return true;
        }
        catch (SocketException ex)
        {
            _log($"{channel} listen {address}:{port} failed: {ex.SocketErrorCode}");
            return false;
        }
    }

    private static List<AdapterEndpoint> CreateEndpointSnapshot(AdapterOptions options, IEnumerable<AdapterEndpoint> endpoints)
    {
        if (options.GameAdapterPort is <= 0 or > ushort.MaxValue)
            throw new InvalidOperationException("登录端口必须为 1-65535。");
        if (options.WorldAdapterPort is <= 0 or > ushort.MaxValue)
            throw new InvalidOperationException("世界端口必须为 1-65535。");

        var snapshot = endpoints.Select(item => new AdapterEndpoint
        {
            Id = item.Id,
            Name = (item.Name ?? string.Empty).Trim(),
            Host = (item.Host ?? string.Empty).Trim(),
            Port = options.WorldAdapterPort,
            Enabled = item.Enabled,
            Catalog = (item.Catalog ?? string.Empty).Trim(),
            Capacity = item.Capacity
        }).ToList();
        var enabled = snapshot.Where(item => item.Enabled).OrderBy(item => item.Id).ToArray();
        if (enabled.Length == 0)
            throw new InvalidOperationException("至少需要启用一个频道。");
        if (enabled.Length > 20)
            throw new InvalidOperationException("原生客户端最多支持 20 个频道。");
        for (var index = 0; index < enabled.Length; index++)
        {
            if (enabled[index].Id != index + 1)
                throw new InvalidOperationException("启用频道 ID 必须从 1 开始连续排列，客户端按 ID 生成频道名称。");
            if (!IPAddress.TryParse(enabled[index].Host, out var hostAddress)
                || hostAddress.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException($"频道 {enabled[index].Id} 地址必须是有效的 IPv4 地址。");
            if (enabled[index].Capacity != 1000)
                throw new InvalidOperationException($"频道 {enabled[index].Id} 容量必须为原生客户端固定值 1000。");
        }
        if (enabled.Length > 1 && enabled.Select(item => item.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() != enabled.Length)
            throw new InvalidOperationException("多频道必须使用不同的 IPv4 地址，客户端通过目标地址选择频道。");
        return snapshot;
    }

    public async Task StopAsync()
    {
        var ownedWorldSessionIds = _activeWorldSessions.Keys.ToArray();
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        List<(TcpListener Listener, string Channel, int Port)> items;
        lock (_listeners)
        {
            items = _listeners.ToList();
            _listeners.Clear();
        }
        foreach (var item in items) item.Listener.Stop();
        if (cts is not null)
        {
            var villageBotTask = Interlocked.Exchange(ref _villageBotTask, null);
            if (villageBotTask is not null)
            {
                try { await villageBotTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log($"Stopping village bots failed: {ex.Message}"); }
            }
            var clientTasks = _clientTasks.Values.ToArray();
            if (clientTasks.Length > 0)
            {
                try { await Task.WhenAll(clientTasks); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log($"停止连接时发生异常：{ex.Message}"); }
            }
            cts.Dispose();
        }
        _loginTicketsByUsername.Clear();
        _loginTicketsByCharacterName.Clear();
        _loginTicketsByAccountId.Clear();
        _pendingLaunchTickets.Clear();
        _seenAuthNonces.Clear();
        _authFailures.Clear();
        _activeWorldSessions.Clear();
        _mentorAdvertisingCharacters.Clear();
        MentorPendingRequest[] abandonedMentorRequests;
        lock (_mentorGate)
        {
            abandonedMentorRequests = _mentorPendingRequests.Values.ToArray();
            _mentorPendingRequests.Clear();
        }
        lock (_coupleGate)
            _couplePendingRequests.Clear();
        lock (_villageBotGate)
        {
            _villageBotGroups.Clear();
            _villageBotRecentMessages.Clear();
        }
        lock (_dungeonRoomGate)
            _dungeonRooms.Clear();
        foreach (var pending in abandonedMentorRequests)
        {
            try { await _database.CompleteMentorInteractionAsync(pending.InteractionId, MentorProtocol.TimedOut); }
            catch (Exception ex) { _log($"停止服务时收口师生请求失败：interaction={pending.InteractionId} error={ex.Message}"); }
        }
        try { await _database.DeactivateAllMentorAdvertisementsAsync(); }
        catch (Exception ex) { _log($"停止服务时清理家教广告失败：{ex.Message}"); }
        try { await _database.ResetOnlineStatesForSessionsAsync(ownedWorldSessionIds); }
        catch (Exception ex) { _log($"閲嶇疆鍦ㄧ嚎鐘舵€佸け璐ワ細{ex.Message}"); }
        AccountStateChanged?.Invoke();
        MentorStateChanged?.Invoke();
        if (items.Count > 0) _log("TCP 服务已停止。");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _loginAuth.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, string channel, int port, int channelId, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                ConfigureAcceptedClient(client);
                var taskId = Interlocked.Increment(ref _nextClientTaskId);
                var task = HandleClientAsync(client, channel, port, channelId, token);
                _clientTasks[taskId] = task;
                _ = task.ContinueWith(
                    completedTask => _clientTasks.TryRemove(taskId, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { _log($"{channel}:{port} accept loop failed: {ex.Message}"); }
    }

    private async Task HandleClientAsync(TcpClient client, string channel, int port, int channelId, CancellationToken token)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
        using (client)
        await using (var stream = client.GetStream())
        {
            _log($"{channel}:{port} 客户端连接 {remote}");
            var initialBytes = new byte[4];
            if (!await ReadExactlyAsync(stream, initialBytes, token))
                return;
            if (initialBytes.AsSpan().SequenceEqual(FriendBridgeMagic))
            {
                await HandleFriendBridgeAsync(stream, channel, port, remote, remoteIp, token);
                return;
            }

            ushort lastOpcode = 0;
            var protocolStage = "connected";
            var session = new ConnectionSession { ChannelId = channelId, ListenerPort = port };
            session.Stream = stream;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                session.ConnectionCancellation = linked;
                session.OutboundWriterTask = RunOutboundWriterAsync(session, stream, linked.Token);
                // The connection lifetime is sliding-idle, not an absolute 30-minute cap.
                // TouchSessionActivity resets this timer after every inbound frame and
                // every successfully written outbound frame.
                TouchSessionActivity(session);
                while (!linked.IsCancellationRequested)
                {
                    // game.exe connects directly to GameAdapter:11005 and sends
                    // the native 8-byte application header.  The first four
                    // bytes are transport controls, +4 is the total frame
                    // length, and +6 is the little-endian opcode.
                    var nativeRead = await TryReadNativeFrameAsync(stream, initialBytes, linked.Token);
                    initialBytes = [];
                    if (!nativeRead.Success)
                        break;
                    var nativeFrame = nativeRead.Frame;
                    var nativeOpcode = nativeRead.Opcode;
                    TouchSessionActivity(session);
                    if (session.OnlineTracked
                        && _activeWorldSessions.TryGetValue(session.SessionId, out var presence))
                        presence.Touch(session.LastActivityUtc);
                    lastOpcode = nativeOpcode;
                    protocolStage = GetProtocolStage(nativeOpcode);

                    var isAuthenticationControl = nativeOpcode is
                        LoginAuthProtocol.PublicKeyRequestOpcode or
                        LoginAuthProtocol.AuthenticateRequestOpcode;
                    if (!isAuthenticationControl && !session.ResponseTransportInitialized)
                    {
                        session.ResponseTransportXorKey = nativeFrame[0];
                        session.ResponseTransportInitialized = true;
                    }

                    if (isAuthenticationControl)
                    {
                        // The launcher uses the native length/opcode envelope,
                        // but not the game's transport-tag sequence.
                    }
                    else if (TryReadTransportXorKey(nativeFrame, out var transportXorKey))
                        session.ClientTransportXorKey = transportXorKey;
                    else
                        _log($"{channel}:{port} {remote} 鍘熺敓甯т紶杈撴牎楠屽紓甯革紝缁х画璁板綍浠ヤ究鍒嗘瀽");

                    var nativePayload = nativeFrame.AsMemory(8);
                    if (!IsHighFrequencyOpcode(nativeOpcode))
                        _log($"{channel}:{port} {remote} 收到原生帧 {nativeFrame.Length} 字节 opcode=0x{nativeOpcode:X4} payload={nativePayload.Length} HEX={FormatNativeFrameHexForLog(nativeFrame, nativeOpcode)}");
                    var nativeResponse = await HandleNativeFrameAsync(nativeFrame, nativeOpcode, channel, remote, remoteIp, session, linked.Token);
                    if (nativeResponse is not null)
                    {
                        await QueueOutboundWriteAsync(
                            session,
                            new OutboundNativeWrite(
                                nativeResponse,
                                channel,
                                port,
                                remote,
                                !isAuthenticationControl,
                                false,
                                null),
                            linked.Token);
                    }
                    await FlushPendingBroadcastsAsync(session, linked.Token);
                }
                if (!token.IsCancellationRequested)
                    _log($"{channel}:{port} {remote} 客户端发送 FIN；阶段={protocolStage} lastOpcode=0x{lastOpcode:X4}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (OperationCanceledException)
            {
                var reason = session.DisconnectReason ?? "连接等待超时";
                _log($"{channel}:{port} {remote} {reason}；阶段={protocolStage} lastOpcode=0x{lastOpcode:X4}");
            }
            catch (SocketException ex)
            {
                _log($"{channel}:{port} {remote} Socket 断开 {ex.SocketErrorCode}；阶段={protocolStage} lastOpcode=0x{lastOpcode:X4}");
            }
            catch (IOException ex) when (ex.InnerException is SocketException socketException)
            {
                _log($"{channel}:{port} {remote} TCP 断开 {socketException.SocketErrorCode}；阶段={protocolStage} lastOpcode=0x{lastOpcode:X4}");
            }
            catch (IOException ex)
            {
                _log($"{channel}:{port} {remote} IO 断开：{ex.Message}；阶段={protocolStage} lastOpcode=0x{lastOpcode:X4}");
            }
            catch (Exception ex)
            {
                _log($"{channel}:{port} {remote} 会话异常：{ex.Message}；阶段={protocolStage} lastOpcode=0x{lastOpcode:X4}");
            }
            finally
            {
                if (string.Equals(channel, "WorldAdapter", StringComparison.Ordinal)
                    && session.OnlineTracked
                    && session.Character is not null)
                {
                    CacheLoginTicket(session, channelReentry: true);
                    _log($"{channel}:{port} {remote} cached disconnect-first channel handoff ticket account={session.AccountId} character={session.Character.Name}");
                }
                try { await CloseNativeDungeonAsync(session); }
                catch (Exception ex) { _log($"Native dungeon disconnect checkpoint failed: {ex.Message}; inspect {NativeJournalDirectory}"); }
                session.ConnectionCancellation = null;
                QueueTownDisconnectNotification(session);
                LeaveTradeRoomScene(session, "trade-room connection leave");
                LeavePartyScene(session, 20);
                LeaveApartmentScene(session, "apartment connection leave");
                LeaveVillageShopScene(session, "shop connection leave");
                QueueDungeonDisconnectNotification(session);
                if (IsEntertainmentSession(channel, session))
                    QueueEntertainmentDisconnectNotification(session);
                else
                    QueueArenaDisconnectNotification(session);
                await FlushPendingBroadcastsAsync(session, token);
                await TrackDisconnectedAsync(session);
                session.OutboundWrites.Writer.TryComplete();
                if (session.OutboundWriterTask is not null)
                {
                    try { await session.OutboundWriterTask; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }
                session.Stream = null;
                _log($"{channel}:{port} 瀹㈡埛绔柇寮€ {remote}");
            }
        }
    }

    private static void ConfigureAcceptedClient(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            client.ReceiveBufferSize = SocketBufferSize;
            client.SendBufferSize = SocketBufferSize;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // A peer can close immediately after accept; the connection loop
            // will observe it without taking down the shared accept loop.
        }
    }

    private static async Task<(bool Success, byte[] Frame, ushort Opcode)> TryReadNativeFrameAsync(
        NetworkStream stream,
        byte[] initialBytes,
        CancellationToken token)
    {
        var header = new byte[8];
        if (initialBytes.Length == 4)
        {
            initialBytes.CopyTo(header, 0);
            if (!await ReadExactlyAsync(stream, header.AsMemory(4, 4), token))
                return (false, [], 0);
        }
        else if (!await ReadExactlyAsync(stream, header, token))
        {
            return (false, [], 0);
        }

        var totalLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2));
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        // A native frame always contains the complete header.  Reject an
        // invalid length before allocating; this also protects the GUI adapter
        // from interpreting a malformed connection as a multi-megabyte frame.
        if (totalLength < 8 || totalLength > 0xFFFF)
            return (false, [], opcode);

        var frame = new byte[totalLength];
        header.CopyTo(frame, 0);
        var success = await ReadExactlyAsync(stream, frame.AsMemory(8), token);
        return (success, success ? frame : [], opcode);
    }

    private async Task<byte[]?> HandleNativeFrameAsync(
        byte[] frame,
        ushort opcode,
        string channel,
        string remote,
        string? remoteIp,
        ConnectionSession session,
        CancellationToken token)
    {
        var payload = frame[8..];
        if (opcode == 0xCF95)
            return await HandleDungeonRevivalRetryAsync(frame, channel, remote, session, payload, token);
        if (await RouteNativeDungeonAsync(frame, opcode, channel, session, token))
            return null;
        var requiredChannel = GetRequiredInboundChannel(opcode);
        if (requiredChannel is not null
            && !string.Equals(channel, requiredChannel, StringComparison.Ordinal)
            && !(string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal)
                 && IsArenaAdapterOpcode(opcode)))
        {
            _log($"{channel}:{remote} 协议端口异常：opcode=0x{opcode:X4} 只能由 {requiredChannel} 接收；未响应且未修改存档");
            return null;
        }

        switch (opcode)
        {
            case LoginAuthProtocol.PublicKeyRequestOpcode:
                if (payload.Length != 0)
                    return BuildAuthenticationControlFrame(
                        LoginAuthProtocol.AuthenticateResponseOpcode,
                        LoginAuthProtocol.BuildResponse(0, 0, DateTime.UnixEpoch, "认证请求无效。"));
                return BuildAuthenticationControlFrame(
                    LoginAuthProtocol.PublicKeyResponseOpcode,
                    _loginAuth.ExportPublicKey());

            case LoginAuthProtocol.AuthenticateRequestOpcode:
                return await HandleLauncherAuthenticationAsync(payload, remoteIp, token);

            case 0x2713: // login request -> login result
            {
                if (payload.Length != NativeLoginPayloadLength)
                {
                    _log($"{channel}:{remote} 原生登录包长度无效：期望 {NativeLoginPayloadLength}，实际 {payload.Length}；拒绝登录且未修改存档");
                    ClearAuthenticatedSession(session);
                    return BuildNativeFrame(frame, 0x2714, BuildLoginResponsePayload(0, session), session);
                }

                PruneAuthenticationState();
                if (!TryParseNativeLoginCredentials(payload, out var username, out var password))
                {
                    ClearAuthenticatedSession(session);
                    _log($"{channel}:{remote} 原生登录字段无效；账号必须为 6-12 位数字，密码必须为 6-16 个有效字符");
                    return BuildNativeFrame(frame, 0x2714, BuildLoginResponsePayload(2, session), session);
                }

                var throttleKey = $"native|{remoteIp ?? "unknown"}|{username}";
                try
                {
                    var loginAccess = await _database.GetLoginAccessAsync(username, remoteIp, token);
                    var blockedStatus = loginAccess.IpBanMode == IpBanMode.Double
                        ? AccountAuthenticationStatus.DoubleBanned
                        : loginAccess.AccountBanned
                            ? AccountAuthenticationStatus.Banned
                            : loginAccess.IpBanMode == IpBanMode.IpOnly
                                ? AccountAuthenticationStatus.IpBanned
                                : (AccountAuthenticationStatus?)null;
                    // A double-banned IP with a not-yet-banned account must continue
                    // through password verification so a valid login can ban that account.
                    if (blockedStatus is AccountAuthenticationStatus.DoubleBanned
                        && !loginAccess.AccountBanned)
                        blockedStatus = null;
                    if (blockedStatus is not null)
                    {
                        _authFailures.TryRemove(throttleKey, out _);
                        ClearAuthenticatedSession(session);
                        _log($"{channel}:{remote} 鍘熺敓鐧诲綍澶辫触 account={username} result={blockedStatus}");
                        return BuildNativeFrame(
                            frame,
                            0x2714,
                            BuildLoginResponsePayload(GetNativeLoginFailureCode(blockedStatus.Value), session),
                            session);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ClearAuthenticatedSession(session);
                    _log($"{channel}:{remote} 原生登录封禁状态读取失败 account={username} error={ex.GetType().Name}");
                    return BuildNativeFrame(frame, 0x2714, BuildLoginResponsePayload(0, session), session);
                }

                if (IsAuthenticationBlocked(throttleKey, out var retryAfter))
                {
                    ClearAuthenticatedSession(session);
                    _log($"{channel}:{remote} 原生登录尝试次数过多 account={username} retryAfter={retryAfter}s");
                    return BuildNativeFrame(frame, 0x2714, BuildLoginResponsePayload(0, session), session);
                }

                AccountAuthenticationResult result;
                try
                {
                    result = await _database.AuthenticateOrRegisterAsync(username, password, remoteIp, _loginPolicy, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ClearAuthenticatedSession(session);
                    _log($"{channel}:{remote} 原生登录数据库异常 account={username} error={ex.GetType().Name}");
                    return BuildNativeFrame(frame, 0x2714, BuildLoginResponsePayload(0, session), session);
                }

                if (!result.Success)
                {
                    if (result.Status == AccountAuthenticationStatus.Failed)
                        RecordAuthenticationFailure(throttleKey);
                    ClearAuthenticatedSession(session);
                    _log($"{channel}:{remote} 鍘熺敓鐧诲綍澶辫触 account={username} result={result.Status}");
                    if (result.AccountId > 0
                        && result.Status is AccountAuthenticationStatus.DoubleBanned
                            or AccountAuthenticationStatus.AuthorizationRequired)
                        AccountStateChanged?.Invoke();
                    return BuildNativeFrame(
                        frame,
                        0x2714,
                        BuildLoginResponsePayload(GetNativeLoginFailureCode(result.Status), session),
                        session);
                }

                _authFailures.TryRemove(throttleKey, out _);
                session.AccountId = result.AccountId;
                session.Username = result.Username;
                session.RemoteIp = remoteIp;
                session.Character = await _database.GetCharacterAsync(session.AccountId, token);
                CacheLoginTicket(session);
                if (result.Status == AccountAuthenticationStatus.Registered)
                    AccountStateChanged?.Invoke();
                _log($"{channel}:{remote} 原生登录成功 account={result.Username} accountId={result.AccountId} result={result.Status} character={(session.Character is null ? "none" : session.Character.Name)}");
                return BuildNativeFrame(frame, 0x2714, BuildLoginResponsePayload(1, session), session);
            }

            case 0x2717: // character creation -> save and acknowledge
            {
                if (session.AccountId <= 0)
                {
                    _log($"{channel}:{remote} 拒绝未登录连接创建角色");
                    return BuildNativeFrame(frame, 0x2718, BuildCharacterCreationResultPayload(false), session);
                }
                if (payload.Length != CharacterCreationPayloadLength)
                {
                    _log($"{channel}:{remote} 创建角色包长度无效：期望 {CharacterCreationPayloadLength}，实际 {payload.Length}；未修改角色存档");
                    return BuildNativeFrame(frame, 0x2718, BuildCharacterCreationResultPayload(false), session);
                }
                var creation = ParseCharacterCreation(payload);
                if (NativeDungeonEnabled && Encoding.GetEncoding(936).GetByteCount(creation.Name) > 14)
                    return BuildNativeFrame(frame, 0x2718, BuildCharacterCreationResultPayload(false), session);
                var result = await _database.CreateCharacterAsync(session.AccountId, creation.Name, creation.Gender, creation.Face, creation.Appearance, token);
                if (result.Success)
                {
                    session.Character = await _database.GetCharacterAsync(session.AccountId, token);
                    CacheLoginTicket(session);
                    AccountStateChanged?.Invoke();
                }
                _log($"{channel}:{remote} 创建角色 name={creation.Name} result={(result.Success ? "success" : result.Error)}");
                return BuildNativeFrame(frame, 0x2718, BuildCharacterCreationResultPayload(result.Success), session);
            }

            case 0x2725: // recommend an existing character -> recommendation result
            {
                if (session.AccountId <= 0 || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未完成角色创建的好友推荐请求");
                    return null;
                }
                if (payload.Length != FriendRecommendationRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 好友推荐包长度无效：期望 {FriendRecommendationRequestPayloadLength}，实际 {payload.Length}；未修改推荐记录");
                    return null;
                }
                if (!TryDecodeFixedGbkString(payload, false, out var recommendedCharacterName))
                {
                    _log($"{channel}:{remote} 好友推荐角色名字段无效；未修改推荐记录");
                    return BuildNativeFrame(
                        frame,
                        0x2726,
                        BuildFriendRecommendationResultPayload(FriendRecommendationNonexistent, null),
                        session);
                }

                var recommendation = await _database.RecommendFriendAsync(
                    session.AccountId,
                    recommendedCharacterName,
                    token);
                var resultCode = recommendation.Status switch
                {
                    FriendRecommendationStatus.Nonexistent => FriendRecommendationNonexistent,
                    FriendRecommendationStatus.Self => FriendRecommendationSelf,
                    FriendRecommendationStatus.Success => FriendRecommendationSuccess,
                    _ => throw new InvalidOperationException($"Unknown friend recommendation result: {recommendation.Status}")
                };
                _log($"{channel}:{remote} 好友推荐 requester={session.Character.Name} target={recommendedCharacterName} result={recommendation.Status} storedTarget={recommendation.RecommendedCharacterName ?? "none"}");
                var recommendationResult = BuildNativeFrame(
                    frame,
                    0x2726,
                    BuildFriendRecommendationResultPayload(resultCode, recommendation.RecommendedCharacterName),
                    session);
                // Retail closes CMakeCharState after the success dialog, then
                // sends a fresh 0x2719. Its normal handler supplies 0x271A.
                return recommendationResult;
            }

            case 0x2719: // post-login client/version context
                if (session.AccountId <= 0 || payload.Length != PostLoginPayloadLength)
                {
                    _log($"{channel}:{remote} 拒绝登录上下文握手：accountId={session.AccountId} payload={payload.Length}，期望已登录且负载 {PostLoginPayloadLength} 字节");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                CacheLoginTicket(session);
                _log($"{channel}:{remote} 登录上下文握手完成 account={session.AccountId}");
                return BuildNativeFrame(frame, 0x271A, BuildPostLoginPayload(session), session);

            case 0x271B: // channel list request
                if (payload.Length != ChannelListRequestPayloadLength)
                {
                    _log($"{channel}:{remote} rejected channel-list payload={payload.Length}; expected {ChannelListRequestPayloadLength}");
                    return null;
                }
                if (session.AccountId > 0)
                {
                    CacheLoginTicket(session);
                }
                else
                {
                    var reentryMatches = CacheUnambiguousChannelReentryTicket(remoteIp);
                    _log($"{channel}:{remote} fresh channel-list reentry candidates={reentryMatches}");
                }
                return BuildNativeFrame(frame, 0x271C, BuildChannelListPayload(), session);

            case 0x2732: // game restriction/anti-addiction check
                // The client reads packet+8 as a uint32 and treats any
                // non-zero value as an account-lock flag.
                if (session.AccountId <= 0 || payload.Length != RestrictionCheckPayloadLength)
                {
                    _log($"{channel}:{remote} 拒绝游戏限制检查：accountId={session.AccountId} payload={payload.Length}，期望已登录且负载 {RestrictionCheckPayloadLength} 字节");
                    return null;
                }
                return BuildNativeFrame(frame, 0x2733, new byte[4], session);

            case 0x2730: // legacy Tencent launcher login context
            {
                if (payload.Length != LegacyTencentLoginPayloadLength)
                {
                    _log($"{channel}:{remote} 登录器兼容包长度无效：期望 {LegacyTencentLoginPayloadLength}，实际 {payload.Length}；未响应且未修改存档");
                    return null;
                }

                if (await TryLocalLauncherLoginAsync(session, remoteIp, token))
                    return BuildNativeFrame(frame, 0x2731, new byte[] { 1, 0, 1, 0 }, session);
                var uin = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var token1Start = 4;
                var token2Start = token1Start + LegacyTencentToken1Length;
                var token1Terminator = Array.IndexOf(payload, (byte)0, token1Start, LegacyTencentToken1Length);
                var token2Terminator = Array.IndexOf(payload, (byte)0, token2Start, LegacyTencentToken2Length);
                var token1Length = token1Terminator < 0 ? -1 : token1Terminator - token1Start;
                var token2Length = token2Terminator < 0 ? -1 : token2Terminator - token2Start;
                if (token1Length < 0 || token2Length < 0)
                {
                    _log($"{channel}:{remote} 登录器兼容包字符串槽缺少 NUL 终止符：uin={uin} token1Terminated={token1Length >= 0} token2Terminated={token2Length >= 0}；未响应且未修改存档");
                    return null;
                }

                _log($"{channel}:{remote} 收到已停用的不受支持的登录器协议：uin={uin} token1Length={token1Length} token2Length={token2Length}；令牌内容未记录、未入库、未响应，请使用直连账号协议 0x2713");
                return null;
            }

            case 0xC351: // channel connection -> player/tutorial context
            {
                if (payload.Length != WorldConnectPayloadLength)
                {
                    _log($"{channel}:{remote} 世界连接包长度无效：期望 {WorldConnectPayloadLength}，实际 {payload.Length}；拒绝恢复世界会话");
                    return BuildNativeFrame(frame, 0xC352, BuildChannelConnectionPayload(null, false), session);
                }

                if (!TryDecodeWorldIdentity(payload, out var identityText))
                {
                    _log($"{channel}:{remote} 世界连接包缺少有效的 NUL 结尾 GBK 角色名；拒绝恢复世界会话");
                    return BuildNativeFrame(frame, 0xC352, BuildChannelConnectionPayload(null, false), session);
                }
                var restored = await RestoreWorldSessionAsync(identityText, remoteIp, session, token);
                _log($"{channel}:{remote} 世界会话恢复 identity={identityText} accountId={session.AccountId} result={(restored ? "success" : "failed")} {FormatCharacterRestoreSummary(session.Character)}");
                return BuildNativeFrame(frame, 0xC352, BuildChannelConnectionPayload(session.Character, restored), session);
            }

            case 0xC354: // load-necessity request
                if (!session.OnlineTracked) return null;
                if (payload.Length != 0)
                {
                    _log($"{channel}:{remote} 必需资源请求包含意外负载 {payload.Length} 字节；未响应且未修改角色存档");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                var persistedDungeonClearMasks = session.Character is null
                    ? new byte[60]
                    : await _database.GetDungeonClearMasksAsync(session.Character.Id, token);
                var dungeonClearMasks = BuildClientDungeonClearMasks(persistedDungeonClearMasks);
                var persistedDungeonBestRatings = session.Character is null
                    ? new byte[60]
                    : await _database.GetDungeonBestRatingsAsync(session.Character.Id, token);
                var dungeonBestRatings = BuildClientDungeonBestRatings(persistedDungeonBestRatings);
                var dungeonSecretBestRatings = session.Character is null
                    ? new byte[C355SecretRatingsLength]
                    : await _database.GetDungeonSecretBestRatingsAsync(session.Character.Id, token);
                var coupleRelation = session.Character is null
                    ? null
                    : await _database.GetActiveCoupleRelationAsync(session.Character.Id, token);
                _log($"{channel}:{remote} 必需资源恢复：{FormatCharacterRestoreSummary(session.Character)}；下发 C355 后触发 C476 库存初始化");
                return BuildLoadNecessityResponse(
                    frame,
                    session,
                    dungeonClearMasks,
                    dungeonBestRatings,
                    dungeonSecretBestRatings,
                    coupleRelation);

            case 0xC358: // ENTER_OZVILL one-way notification
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != 0)
                {
                    _log($"{channel}:{remote} 进入 Oz Village 通知长度无效：期望 0，实际 {payload.Length}；未响应且未修改存档");
                    return null;
                }
                _log($"{channel}:{remote} 鏀跺埌杩涘叆 Oz Village 鍗曞悜閫氱煡锛涘鎴风鏋勯€犲櫒瀹氫箟鎬婚暱 8 瀛楄妭锛屾棤闇€鍝嶅簲");
                return null;

            case 0xC387: // arena entry one-way notification
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != 4)
                {
                    _log($"{channel}:{remote} Arena adapter entry notification length invalid: expected=4 actual={payload.Length}; no response");
                    return null;
                }

                var arenaGameType = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                if (arenaGameType is < 1 or > ArenaGameAdapterTypeCount)
                {
                    _log($"{channel}:{remote} Arena adapter entry notification type invalid: type={arenaGameType}; no response");
                    return null;
                }

                session.ArenaGameType = checked((byte)arenaGameType);
                _log($"{channel}:{remote} Arena adapter entry notification accepted: type={arenaGameType} character={session.Character.Name}; retail protocol is one-way, no response required");
                return null;
            }

            case 0xC388: // arena endpoint request -> response
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != ArenaAdapterRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 天空竞技场 adapter 请求长度无效：期望 {ArenaAdapterRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }

                // The retail constructor only initializes the first ushort.
                // The second word is stack residue and must not be treated as a zone.
                var gameTypeValue = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var reservedWord = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                if (gameTypeValue is < 1 or > 4)
                {
                    _log($"{channel}:{remote} 天空竞技场类型无效：type={gameTypeValue} reserved={reservedWord}；仅接受客户端按钮定义的 1..4，未响应");
                    return null;
                }

                var endpoint = _endpoints.FirstOrDefault(item => item.Enabled && item.Id == session.ChannelId)
                    ?? _endpoints.Where(item => item.Enabled).OrderBy(item => item.Id).FirstOrDefault();
                if (endpoint is null
                    || endpoint.Port is <= 0 or > ushort.MaxValue
                    || !IPAddress.TryParse(endpoint.Host, out var arenaAddress)
                    || arenaAddress.AddressFamily != AddressFamily.InterNetwork)
                {
                    _log($"{channel}:{remote} 天空竞技场没有可用的 IPv4 世界服端点；type={gameTypeValue}，未响应");
                    return null;
                }

                var load = (ushort)Math.Clamp(GetChannelPopulation(endpoint.Id), 0, ushort.MaxValue);
                const byte zone = 1;
                var arenaPayload = BuildArenaGameAdapterPayload(
                    checked((byte)gameTypeValue),
                    zone,
                    arenaAddress.ToString(),
                    checked((ushort)endpoint.Id),
                    load);
                _log($"{channel}:{remote} 返回天空竞技场频道：type={gameTypeValue} zone={zone} channelId={endpoint.Id} reserved={reservedWord} address={arenaAddress} arenaPort={GetArenaGameAdapterPort(checked((byte)gameTypeValue))} load={load}");
                return BuildNativeFrame(frame, 0xC389, arenaPayload, session);
            }

            case 0xC3CB: // REQ_MY_AVATAITEM_LIST
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != EmptyInventoryRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 外观库存请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                var avatarInventoryPayload = BuildAvatarInventoryPayload(session.Character);
                _log($"{channel}:{remote} 恢复外观库存：{FormatAvatarInventorySummary(avatarInventoryPayload)} appearance={FormatAppearanceHex(session.Character?.Appearance)}");
                return BuildNativeFrame(frame, 0xC3CC, avatarInventoryPayload, session);

            case 0xC3D1: // REQ_SURGERY_FACE -> ANS_SURGERY_FACE
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                // The 76-byte C3D1 constructor writes its trailing words at
                // frame +0x44/+0x46/+0x48/+0x4A, i.e. payload +60/+62/+64/+66.
                if (payload.Length != FaceCouponRequestPayloadLength
                    || BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(60, 2)) != 0
                    || BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(62, 2)) != 0
                    || BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(64, 2)) != 1)
                {
                    _log($"{channel}:{remote} face-coupon request layout invalid: payload={payload.Length}; inventory and appearance unchanged");
                    return BuildNativeFrame(frame, 0xC3D2, BuildFaceCouponResultPayload(false, []), session);
                }

                var requestedAppearance = payload.AsSpan(0, 36).ToArray();
                var inventorySlot = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(66, 2));
                await RefreshSessionCharacterAsync(session, token);
                var gameItems = GetGameInventoryItemCodes(session.Character);
                if (inventorySlot >= gameItems.Length
                    || !ShopCatalog.TryGet(gameItems[inventorySlot], out var faceCoupon)
                    || faceCoupon.Category != 45)
                {
                    _log($"{channel}:{remote} face-coupon inventory slot invalid: slot={inventorySlot}/{gameItems.Length}; inventory and appearance unchanged");
                    return BuildNativeFrame(frame, 0xC3D2, BuildFaceCouponResultPayload(false, []), session);
                }

                var use = await _database.UseFaceCouponAsync(
                    session.AccountId,
                    session.Character!.Id,
                    session.SessionId,
                    faceCoupon.ItemCode,
                    requestedAppearance,
                    token);
                if (!use.Success)
                {
                    _log($"{channel}:{remote} face-coupon use rejected: item={faceCoupon.ItemCode} slot={inventorySlot} quantity={use.Quantity} error={use.Error}; inventory and appearance unchanged");
                    return BuildNativeFrame(frame, 0xC3D2, BuildFaceCouponResultPayload(false, []), session);
                }

                await RefreshSessionCharacterAsync(session, token);
                _log($"{channel}:{remote} face-coupon used: item={faceCoupon.ItemCode} name={faceCoupon.Name} slot={inventorySlot} remaining={use.Quantity} appearance={Convert.ToHexString(use.Appearance)}");
                AccountStateChanged?.Invoke();
                var surgeryResult = BuildNativeFrame(
                    frame,
                    0xC3D2,
                    BuildFaceCouponResultPayload(true, use.Appearance),
                    session);
                if (session.Character is null)
                    return surgeryResult;
                var changedAppearance = BuildUserDataChangePayload(session.Character);
                QueueSceneBroadcast(session, 0xC47F, changedAppearance, "face-coupon appearance change");
                return CombineNativeFrames(
                    surgeryResult,
                    BuildNativeFrame(frame, 0xC47F, changedAppearance, session));
            }

            case 0xC3CF: // REQ_DEL_AVATAITEM -> ANS_DEL_AVATAITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != AvatarDeleteRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 衣物丢弃请求长度无效：期望 {AvatarDeleteRequestPayloadLength}，实际 {payload.Length}；库存未修改");
                    return BuildNativeFrame(
                        frame,
                        0xC3D0,
                        BuildSlottedInventoryDeleteResultPayload(false, 0, 0, AvatarDeleteResponsePayloadLength, 10),
                        session);
                }

                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var slot = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                var deleteResult = await _database.DeleteInventoryItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    InventorySection.Clothing,
                    token);
                await RefreshSessionCharacterAsync(session, token);
                _log($"{channel}:{remote} 衣物丢弃：item={itemCode} slot={slot} result={(deleteResult.Success ? "success" : "failed")} remaining={deleteResult.Quantity}");
                if (!deleteResult.Success || session.Character is null)
                {
                    return BuildNativeFrame(
                        frame,
                        0xC3D0,
                        BuildSlottedInventoryDeleteResultPayload(false, itemCode, slot, AvatarDeleteResponsePayloadLength, 10),
                        session);
                }

                AccountStateChanged?.Invoke();
                var deleteFrame = BuildNativeFrame(
                    frame,
                    0xC3D0,
                    BuildSlottedInventoryDeleteResultPayload(true, itemCode, slot, AvatarDeleteResponsePayloadLength, 10),
                    session);
                var changedUserDataPayload = BuildUserDataChangePayload(session.Character);
                QueueSceneBroadcast(session, 0xC47F, changedUserDataPayload, "scene appearance after clothing discard");
                return CombineNativeFrames(
                    deleteFrame,
                    BuildNativeFrame(frame, 0xC47F, changedUserDataPayload, session));
            }

            case 0xC44B: // REQ_MY_PETITEM_LIST
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != EmptyInventoryRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 宠物库存请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                var petInventoryPayload = BuildPetInventoryPayload(session.Character);
                _log($"{channel}:{remote} 恢复宠物库存：variant={session.Character?.PetVariant ?? 0} level={session.Character?.PetLevel ?? 0} exp={session.Character?.PetExperience ?? 0} {FormatPetInventorySummary(petInventoryPayload)}");
                return BuildNativeFrame(frame, 0xC44C, petInventoryPayload, session);

            case 0xC44D: // REQ_DEL_PETITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != PetDeleteRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 宠物丢弃请求长度无效：期望 {PetDeleteRequestPayloadLength}，实际 {payload.Length}；未修改库存");
                    return BuildNativeFrame(frame, 0xC44E, BuildPetDeleteResultPayload(false, 0, 0), session);
                }

                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var slot = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2));
                if (itemCode / 1_000_000 != 15)
                {
                    _log($"{channel}:{remote} 拒绝非宠物丢弃请求：item={itemCode} slot={slot}");
                    return BuildNativeFrame(frame, 0xC44E, BuildPetDeleteResultPayload(false, itemCode, slot), session);
                }

                var deleted = await _database.DeletePetItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    token);
                await RefreshSessionCharacterAsync(session, token);
                _log($"{channel}:{remote} 宠物丢弃：item={itemCode} slot={slot} result={(deleted ? "success" : "failed")} equipped={GetEquippedPetItemCode(session.Character)}");
                if (deleted)
                    AccountStateChanged?.Invoke();
                return BuildNativeFrame(
                    frame,
                    0xC44E,
                    BuildPetDeleteResultPayload(deleted, itemCode, slot),
                    session);
            }

            case 0xC44F: // REQ_CHANGE_PETITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != PetChangeRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 宠物变更长度无效：期望 {PetChangeRequestPayloadLength}，实际 {payload.Length}");
                    return BuildNativeFrame(frame, 0xC450, BuildPetChangeResultPayload(0, 0, false), session);
                }
                var petChangeOperation = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var responseOperation = petChangeOperation <= byte.MaxValue ? (byte)petChangeOperation : (byte)0;
                var responseState = payload[3];
                if (!TryResolvePetChangeRequest(
                        session.Character,
                        payload,
                        out var resolvedOperation,
                        out var petItemCode,
                        out var materialItemCode,
                        out var gameItemIdentity))
                {
                    _log($"{channel}:{remote} Pet change rejected: operation={petChangeOperation} petHandle={payload[2]} itemIdentity={(petChangeOperation == 2 ? payload[5] : payload[4])}");
                    return BuildNativeFrame(frame, 0xC450, BuildPetChangeResultPayload(responseOperation, responseState, false), session);
                }

                petChangeOperation = resolvedOperation;
                responseOperation = checked((byte)resolvedOperation);
                var petSlot = payload[2];
                var changeResult = petChangeOperation is 3 or 4
                    ? await _database.ApplyPetSpecialGemAsync(
                        session.AccountId,
                        session.Character.Id,
                        session.SessionId,
                        responseOperation,
                        petItemCode,
                        payload[3],
                        materialItemCode,
                        token)
                    : await _database.ChangePetItemAsync(
                        session.AccountId,
                        session.Character.Id,
                        session.SessionId,
                        responseOperation,
                        petItemCode,
                        payload[3],
                        materialItemCode,
                        token);
                await RefreshSessionCharacterAsync(session, token);
                _log($"{channel}:{remote} Pet change: operation={petChangeOperation} pet={petItemCode} petHandle={petSlot} material={materialItemCode} itemIdentity={gameItemIdentity} position={payload[3]} result={(changeResult.Success ? "success" : "failed")} error={changeResult.Error}");
                if (changeResult.Success)
                    AccountStateChanged?.Invoke();
                var changeResponse = BuildNativeFrame(
                    frame,
                    0xC450,
                    BuildPetChangeResultPayload(responseOperation, responseState, changeResult.Success),
                    session);
                if (!changeResult.Success || session.Character is null)
                    return changeResponse;

                var petUserData = BuildUserDataChangePayload(session.Character);
                QueueSceneBroadcast(session, 0xC47F, petUserData, "pet accessory/upgrade change");
                return CombineNativeFrames(
                    changeResponse,
                    BuildNativeFrame(frame, 0xC47F, petUserData, session),
                    BuildNativeFrame(
                        frame,
                        0xC379,
                        BuildBoxInfoPayloadWithSkills(
                            session.Character,
                            await _database.GetCharacterSkillsAsync(session.Character.Id, token)),
                        session),
                    BuildNativeFrame(frame, 0xC44C, BuildPetInventoryPayload(session.Character), session));
            }

            case 0xC47D: // REQ_CHANGE_INVENTORYITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != InventoryChangeRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 库存穿戴提交长度无效：期望 {InventoryChangeRequestPayloadLength}，实际 {payload.Length}；未修改存档");
                    return BuildNativeFrame(
                        frame,
                        0xC47E,
                        BuildInventoryChangeResultPayload(false),
                        session);
                }

                // The C47D constructor writes the newly selected pet at frame+96,
                // the deselected pet at frame+100, and its 36-byte appearance at +104.
                var quickSlotLayoutValid = TryApplyInventoryQuickSlotDelta(
                    session.Character,
                    payload,
                    out var quickSlots);
                var equippedPetItemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(88, 4));
                var unequippedPetItemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(92, 4));
                var appearance = payload.AsSpan(96, 36).ToArray();
                var appearancePetItemCode = BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(28, 4));
                if (!quickSlotLayoutValid
                    || ((equippedPetItemCode != 0 || unequippedPetItemCode != 0)
                        && appearancePetItemCode != equippedPetItemCode))
                {
                    _log($"{channel}:{remote} 库存穿戴提交被拒绝：appearancePet={appearancePetItemCode} selectedPet={equippedPetItemCode} 不一致");
                    return BuildNativeFrame(
                        frame,
                        0xC47E,
                        BuildInventoryChangeResultPayload(false),
                        session);
                }

                var changed = await _database.SaveInventoryEquipmentAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    quickSlots,
                    equippedPetItemCode,
                    unequippedPetItemCode,
                    appearance,
                    token);
                await RefreshSessionCharacterAsync(session, token);
                _log($"{channel}:{remote} 库存穿戴提交：selectedPet={equippedPetItemCode} deselectedPet={unequippedPetItemCode} result={(changed ? "success" : "rejected")} storedPet={GetEquippedPetItemCode(session.Character)} appearance={FormatAppearanceHex(session.Character?.Appearance)}");
                if (changed)
                    AccountStateChanged?.Invoke();
                var inventoryResult = BuildNativeFrame(
                    frame,
                    0xC47E,
                    BuildInventoryChangeResultPayload(changed),
                    session);
                if (!changed || session.Character is null)
                    return inventoryResult;

                var changedUserDataPayload = BuildUserDataChangePayload(session.Character);
                var suppressUnsafeActorRebuild = session.DungeonRoomId != 0
                    || session.ApartmentOwnerCharacterId > 0;
                if (!suppressUnsafeActorRebuild)
                    QueueSceneBroadcast(session, 0xC47F, changedUserDataPayload, "scene appearance/pet change");
                var inventoryFrames = new List<byte[]> { inventoryResult };
                if (!suppressUnsafeActorRebuild)
                {
                    inventoryFrames.Add(BuildNativeFrame(
                        frame,
                        0xC47F,
                        changedUserDataPayload,
                        session));
                }
                inventoryFrames.Add(BuildNativeFrame(
                    frame,
                    0xC379,
                    BuildBoxInfoPayloadWithSkills(
                        session.Character,
                        await _database.GetCharacterSkillsAsync(session.Character.Id, token)),
                    session));
                inventoryFrames.Add(BuildNativeFrame(frame, 0xC3CC, BuildAvatarInventoryPayload(session.Character), session));
                inventoryFrames.Add(BuildNativeFrame(frame, 0xC44C, BuildPetInventoryPayload(session.Character), session));
                return CombineNativeFrames(inventoryFrames.ToArray());
            }

            case 0xC595: // REQ_TASK_ACTIVATE
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != TaskActivationRequestPayloadLength
                    || BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2)) != 0)
                {
                    _log($"{channel}:{remote} task activation rejected: payload={payload.Length}, reserved field invalid; archive unchanged");
                    return null;
                }

                var taskType = payload[2];
                var runtimeState = payload[3];
                var questId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                if (!QuestCatalog.TryGetQuest(questId, out _))
                {
                    _log($"{channel}:{remote} task activation rejected: unknown official quest={questId}");
                    return null;
                }

                var activation = await _database.ActivateQuestTaskAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    questId,
                    taskType,
                    runtimeState,
                    token);
                if (!activation.Authorized)
                    return null;
                _log($"{channel}:{remote} task activation: quest={questId} type={taskType} state={runtimeState} result={(activation.Success ? "success" : "rejected")}");
                return BuildNativeFrame(
                    frame,
                    0xC596,
                    BuildTaskActivationResultPayload(activation.Success, taskType, runtimeState, questId),
                    session);
            }

            case 0xC597: // REQ_TASK_ABANDON
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != TaskAbandonRequestPayloadLength)
                {
                    _log($"{channel}:{remote} task abandon rejected: expected={TaskAbandonRequestPayloadLength} actual={payload.Length}; archive unchanged");
                    return null;
                }

                var taskTypeValue = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var runtimeStateValue = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var questId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                if (taskTypeValue > byte.MaxValue
                    || runtimeStateValue > byte.MaxValue
                    || !QuestCatalog.TryGetQuest(questId, out _))
                {
                    _log($"{channel}:{remote} task abandon rejected: quest={questId} type={taskTypeValue} state={runtimeStateValue}; archive unchanged");
                    return null;
                }

                var abandon = await _database.AbandonQuestTaskAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    questId,
                    checked((byte)taskTypeValue),
                    runtimeStateValue,
                    token);
                if (!abandon.Authorized)
                    return null;
                _log($"{channel}:{remote} task abandon: quest={questId} type={taskTypeValue} state={runtimeStateValue} result={(abandon.Success ? "success" : "rejected")}");
                return BuildNativeFrame(
                    frame,
                    0xC598,
                    BuildTaskAbandonResultPayload(abandon.Success, questId),
                    session);
            }

            case 0xC599: // REQ_TASK_COMPLETE
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != TaskCompletionRequestPayloadLength)
                {
                    _log($"{channel}:{remote} task completion rejected: expected={TaskCompletionRequestPayloadLength} actual={payload.Length}; archive unchanged");
                    return null;
                }

                var taskTypeValue = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var runtimeStateValue = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var questId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                if (taskTypeValue > byte.MaxValue
                    || runtimeStateValue > byte.MaxValue
                    || !QuestCatalog.TryGetQuest(questId, out var quest)
                    || quest.Rewards.Any(reward => reward.RewardType is not 1 and not 7)
                    || payload.AsSpan(8, 72).IndexOfAnyExcept((byte)0) >= 0)
                {
                    _log($"{channel}:{remote} task completion rejected: quest={questId} type={taskTypeValue} state={runtimeStateValue} reward selection is not valid for the official fixed-reward task");
                    return null;
                }

                var completion = await _database.CompleteQuestTaskAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    questId,
                    checked((byte)taskTypeValue),
                    runtimeStateValue,
                    token);
                if (!completion.Authorized)
                    return null;
                if (completion.Success && completion.Character is not null)
                {
                    session.Character = completion.Character;
                    AccountStateChanged?.Invoke();
                }
                _log($"{channel}:{remote} task completion: quest={questId} type={taskTypeValue} state={runtimeStateValue} result={(completion.Success ? "success" : "rejected")} levels={completion.GainedLevels}");
                return BuildNativeFrame(
                    frame,
                    0xC59A,
                    BuildTaskCompletionResultPayload(
                        completion.Success,
                        questId,
                        completion.Character ?? session.Character,
                        completion.HansChanged,
                        completion.GainedLevels),
                    session);
            }

            case 0xC59B: // REQ_TASK_LIST
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != 0)
                {
                    _log($"{channel}:{remote} task list rejected: expected=0 actual={payload.Length}; no response");
                    return null;
                }
                var tasks = await _database.GetCharacterTasksAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    token);
                _log($"{channel}:{remote} task list: normal={tasks.Count(task => task.SlotType == 0)} fixed={tasks.Count(task => task.SlotType != 0)}");
                return BuildNativeFrame(frame, 0xC59C, BuildTaskListPayload(tasks), session);
            }

            case 0xC59E: // REQ_QUEST_SCROLL_PURCHASE
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != QuestScrollPurchaseRequestPayloadLength)
                {
                    _log($"{channel}:{remote} quest-scroll purchase rejected: expected={QuestScrollPurchaseRequestPayloadLength} actual={payload.Length}; archive unchanged");
                    return null;
                }

                var scrollCode = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                if (!QuestCatalog.TryGetScroll(scrollCode, out var scroll))
                {
                    _log($"{channel}:{remote} quest-scroll purchase rejected: unknown official scroll={scrollCode}; archive unchanged");
                    return null;
                }

                var purchase = await _database.PurchaseQuestScrollAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    scroll,
                    token);
                if (!purchase.Authorized)
                    return null;
                if (purchase.Status == QuestScrollPurchaseStatus.Success)
                {
                    await RefreshSessionCharacterAsync(session, token);
                    AccountStateChanged?.Invoke();
                }
                _log($"{channel}:{remote} quest-scroll purchase: scroll={scrollCode} quest={scroll.QuestId} price={scroll.Price} result={(uint)purchase.Status} hans={purchase.Hans}");
                return BuildNativeFrame(
                    frame,
                    0xC59F,
                    BuildQuestScrollPurchaseResultPayload(purchase.Status, purchase.Hans),
                    session);
            }

            case 0xC409: // REQ_MY_INTERIORITEM_LIST
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != InteriorInventoryRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 家装库存请求长度无效：期望 {InteriorInventoryRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                var interiorInventoryMode = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                if (interiorInventoryMode is not 10u and not 20u and not 30u)
                {
                    _log($"{channel}:{remote} 家装库存请求模式无效：{interiorInventoryMode}；仅接受客户端处理器定义的 10/20/30，未响应");
                    return null;
                }
                var starterFurnitureGranted = false;
                if (interiorInventoryMode == 20)
                {
                    var starterResult = await _database.EnsureApartmentStarterInventoryAsync(
                        session.AccountId,
                        session.Character.Id,
                        session.SessionId,
                        token);
                    if (!starterResult.Authorized)
                        return null;
                    starterFurnitureGranted = starterResult.Granted;
                }
                await RefreshSessionCharacterAsync(session, token);
                var interiorInventoryPayload = BuildInteriorInventoryPayload((byte)interiorInventoryMode, session.Character);
                _log($"{channel}:{remote} 恢复家装库存：mode={interiorInventoryMode} count={interiorInventoryPayload[3]} starterGranted={starterFurnitureGranted}");
                if (starterFurnitureGranted)
                    AccountStateChanged?.Invoke();
                return BuildNativeFrame(
                    frame,
                    0xC40A,
                    interiorInventoryPayload,
                    session);

            case 0xC40F: // REQ_DEL_INTERIORITEM -> ANS_DEL_INTERIORITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != InteriorDeleteRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 家具丢弃请求长度无效：期望 {InteriorDeleteRequestPayloadLength}，实际 {payload.Length}；库存未修改");
                    return BuildNativeFrame(
                        frame,
                        0xC410,
                        BuildSlottedInventoryDeleteResultPayload(false, 0, 0, InteriorDeleteResponsePayloadLength, 10),
                        session);
                }

                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var slot = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                var deleteResult = await _database.DeleteInventoryItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    InventorySection.Furniture,
                    token);
                if (deleteResult.Success)
                {
                    await RefreshSessionCharacterAsync(session, token);
                    AccountStateChanged?.Invoke();
                }
                _log($"{channel}:{remote} 家具丢弃：item={itemCode} slot={slot} result={(deleteResult.Success ? "success" : "failed")} remaining={deleteResult.Quantity}");
                return BuildNativeFrame(
                    frame,
                    0xC410,
                    BuildSlottedInventoryDeleteResultPayload(deleteResult.Success, itemCode, slot, InteriorDeleteResponsePayloadLength, 10),
                    session);
            }

            case 0xC42F: // REQ_MY_GAMEITEM_LIST
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != EmptyInventoryRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 游戏物品库存请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                return BuildNativeFrame(frame, 0xC430, BuildGameInventoryPayload(session.Character), session);

            case 0xC433: // REQ_DEL_GAMEITEM -> ANS_DEL_GAMEITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != GameItemDeleteRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 游戏道具丢弃请求长度无效：期望 {GameItemDeleteRequestPayloadLength}，实际 {payload.Length}；库存未修改");
                    return BuildNativeFrame(
                        frame,
                        0xC434,
                        BuildSlottedInventoryDeleteResultPayload(false, 0, 0, GameItemDeleteResponsePayloadLength, 8),
                        session);
                }

                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var slot = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
                var deleteResult = await _database.DeleteInventoryItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    InventorySection.GameItem,
                    token);
                if (deleteResult.Success)
                {
                    await RefreshSessionCharacterAsync(session, token);
                    AccountStateChanged?.Invoke();
                }
                _log($"{channel}:{remote} 游戏道具丢弃：item={itemCode} slot={slot} result={(deleteResult.Success ? "success" : "failed")} remaining={deleteResult.Quantity}");
                return BuildNativeFrame(
                    frame,
                    0xC434,
                    BuildSlottedInventoryDeleteResultPayload(deleteResult.Success, itemCode, slot, GameItemDeleteResponsePayloadLength, 8),
                    session);
            }

            case 0xC473: // REQ_MY_CASHITEM_LIST
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != CashInventoryRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 现金/游戏库存分页请求长度无效：期望 {CashInventoryRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                var cashInventoryMode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                if (cashInventoryMode is not (1 or 10))
                {
                    _log($"{channel}:{remote} invalid cash inventory mode={cashInventoryMode}; expected 1 or 10");
                    return null;
                }
                var cashInventoryPage = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                await RefreshSessionCharacterAsync(session, token);
                var cashInventoryPayload = BuildCashInventoryPayload((byte)cashInventoryMode, cashInventoryPage, session.Character);
                _log($"{channel}:{remote} 恢复现金/游戏库存分页：mode={cashInventoryMode} page={cashInventoryPage} responseCount={cashInventoryPayload[3]}");
                return BuildNativeFrame(
                    frame,
                    0xC474,
                    cashInventoryPayload,
                    session);

            case 0xC475: // REQ_MOVE_CASHITEM_TO_INVENTORY
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != CashInboxClaimRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 待领取物品转移长度无效：期望 {CashInboxClaimRequestPayloadLength}，实际 {payload.Length}；未修改库存");
                    return null;
                }

                // The client only initializes frame+8, +10, +12 and +16 in
                // C475. Bytes +9 and +13..+15 are uninitialized padding.
                var sourceMode = payload[0];
                var sourceSlot = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var itemState = payload[4];
                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
                if (sourceMode is not (1 or 10) || !ShopCatalog.TryGet(itemCode, out var claimedCatalogItem))
                {
                    _log($"{channel}:{remote} 待领取物品转移字段无效：mode={sourceMode} slot={sourceSlot} state={itemState} item={itemCode}；未修改库存");
                    return null;
                }

                var claim = await _database.ClaimCashInboxItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    token);
                await RefreshSessionCharacterAsync(session, token);
                _log($"{channel}:{remote} 待领取物品转入正式背包：mode={sourceMode} slot={sourceSlot} state={itemState} item={itemCode} name={claimedCatalogItem.Name} result={(claim.Success ? "success" : "failure")} inbox={claim.InboxQuantity} inventory={claim.InventoryQuantity} error={claim.Error}");

                // Status 3/4 is the client's successful allocation path. It
                // reads a count at frame+10 and that many item codes at +12.
                var claimPayload = new byte[claim.Success ? 8 : 4];
                BinaryPrimitives.WriteUInt16LittleEndian(
                    claimPayload.AsSpan(0, 2),
                    claim.Success ? (sourceMode == 1 ? (ushort)3 : (ushort)4) : (ushort)0);
                if (claim.Success)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(claimPayload.AsSpan(2, 2), 1);
                    BinaryPrimitives.WriteUInt32LittleEndian(claimPayload.AsSpan(4, 4), itemCode);
                }
                return BuildNativeFrame(frame, 0xC476, claimPayload, session);
            }

            case 0xC469: // REQ_MY_TOKEN_LIST
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != TokenInventoryRequestPayloadLength)
                {
                    _log($"{channel}:{remote} token inventory request length invalid: expected {TokenInventoryRequestPayloadLength}, actual {payload.Length}; no response");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                var tokenInventoryPayload = BuildTokenInventoryPayload(session.Character);
                CaptureMikeItemSelectors(session, tokenInventoryPayload);
                _log($"{channel}:{remote} restored token inventory: count={BinaryPrimitives.ReadUInt16LittleEndian(tokenInventoryPayload.AsSpan(2, 2))}");
                return BuildNativeFrame(
                    frame,
                    0xC46A,
                    tokenInventoryPayload,
                    session);

            case 0xC46D: // REQ_USE_TOKENITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != TokenUseRequestPayloadLength)
                {
                    _log($"{channel}:{remote} token use request length invalid: expected {TokenUseRequestPayloadLength}, actual {payload.Length}; inventory unchanged");
                    return null;
                }

                // The fixed C46D payload reuses its second dword by item
                // family. Category 42 copies the C46A token-list selector;
                // categories 14/47/48 copy the C430 inventory identity.
                var tokenItemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var tokenSelector = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                if (!ShopCatalog.TryGet(tokenItemCode, out var tokenCatalogItem)
                    || tokenCatalogItem.Category is not (14 or 42 or 47 or 48))
                {
                    _log($"{channel}:{remote} token use fields invalid: item={tokenItemCode} selector={tokenSelector}; inventory unchanged");
                    return null;
                }

                if (tokenCatalogItem.Category == 14)
                {
                    await RefreshSessionCharacterAsync(session, token);
                    if (!TryResolveGameInventoryIdentity(session.Character, tokenSelector, out var selectedGameItemCode)
                        || selectedGameItemCode != tokenItemCode
                        || tokenCatalogItem.QuickHpRestore == 0 && tokenCatalogItem.QuickMpRestore == 0)
                    {
                        _log($"{channel}:{remote} backpack item identity invalid: item={tokenItemCode} identity={tokenSelector}; inventory unchanged");
                        return BuildNativeFrame(
                            frame,
                            0xC46E,
                            BuildTokenUseResultPayload(false, tokenItemCode, tokenSelector),
                            session);
                    }

                    var consumeResult = await _database.ConsumeDungeonQuickItemAsync(
                        session.AccountId,
                        session.Character!.Id,
                        session.SessionId,
                        tokenItemCode,
                        tokenCatalogItem.QuickHpRestore,
                        tokenCatalogItem.QuickMpRestore,
                        [new DungeonQuickItemTarget(session.AccountId, session.Character.Id, session.SessionId)],
                        token);
                    if (!consumeResult.Success)
                    {
                        _log($"{channel}:{remote} backpack item use rejected: item={tokenItemCode} identity={tokenSelector}");
                        return BuildNativeFrame(
                            frame,
                            0xC46E,
                            BuildTokenUseResultPayload(false, tokenItemCode, tokenSelector),
                            session);
                    }

                    await RefreshSessionCharacterAsync(session, token);
                    AccountStateChanged?.Invoke();
                    _log($"{channel}:{remote} backpack item used: item={tokenItemCode} identity={tokenSelector} remaining={consumeResult.RemainingQuantity} hp={session.Character?.CurrentHp}/{session.Character?.MaxHp} mp={session.Character?.CurrentMp}/{session.Character?.MaxMp}");
                    return BuildNativeFrame(
                        frame,
                        0xC46E,
                        BuildTokenUseResultPayload(true, tokenItemCode, tokenSelector),
                        session);
                }

                if (tokenCatalogItem.Category == 48)
                {
                    await RefreshSessionCharacterAsync(session, token);
                    if (!TryResolveGameInventoryIdentity(
                            session.Character,
                            tokenSelector,
                            out var selectedRevivalItemCode)
                        || selectedRevivalItemCode != tokenItemCode)
                    {
                        _log($"{channel}:{remote} revival activation identity invalid: item={tokenItemCode} identity={tokenSelector}; inventory unchanged");
                        return BuildNativeFrame(
                            frame,
                            0xC46E,
                            BuildTokenUseResultPayload(false, tokenItemCode, tokenSelector),
                            session);
                    }

                    var revivalResult = await _database.ActivateRevivalItemAsync(
                        session.AccountId,
                        session.Character!.Id,
                        session.SessionId,
                        tokenItemCode,
                        token);
                    if (!revivalResult.Success)
                    {
                        _log($"{channel}:{remote} revival activation rejected: item={tokenItemCode} identity={tokenSelector} quantity={revivalResult.Quantity} error={revivalResult.Error}");
                        return BuildNativeFrame(
                            frame,
                            0xC46E,
                            BuildTokenUseResultPayload(false, tokenItemCode, tokenSelector),
                            session);
                    }

                    await RefreshSessionCharacterAsync(session, token);
                    var revivalUsePayload = BuildTokenUseResultPayload(true, tokenItemCode, tokenSelector);
                    _log($"{channel}:{remote} revival item activated: item={tokenItemCode} name={tokenCatalogItem.Name} slot={tokenSelector} added={tokenCatalogItem.TokenUseCount} activeUses={revivalResult.RevivalUseCount} remaining={revivalResult.Quantity}");
                    return BuildNativeFrame(frame, 0xC46E, revivalUsePayload, session);
                }

                if (tokenCatalogItem.Category == 42)
                {
                    if (!session.MikeItemSelectors.TryGetValue(tokenSelector, out var selectedMikeItemCode)
                        || selectedMikeItemCode != tokenItemCode)
                    {
                        _log($"{channel}:{remote} mike activation selector invalid: item={tokenItemCode} selector={tokenSelector} tokenCount={session.MikeItemSelectors.Count}; inventory unchanged");
                        return null;
                    }

                    var mikeResult = await _database.ActivateMikeItemAsync(
                        session.AccountId,
                        session.Character.Id,
                        session.SessionId,
                        tokenItemCode,
                        token);
                    if (!mikeResult.Success)
                    {
                        _log($"{channel}:{remote} mike activation rejected: item={tokenItemCode} selector={tokenSelector} quantity={mikeResult.Quantity} error={mikeResult.Error}; no success response");
                        return null;
                    }

                    await RefreshSessionCharacterAsync(session, token);
                    session.MikeItemSelectors.Remove(tokenSelector);
                    var mikeUsePayload = new byte[12];
                    BinaryPrimitives.WriteUInt32LittleEndian(mikeUsePayload.AsSpan(0, 4), 1);
                    BinaryPrimitives.WriteUInt32LittleEndian(mikeUsePayload.AsSpan(4, 4), tokenItemCode);
                    BinaryPrimitives.WriteUInt32LittleEndian(mikeUsePayload.AsSpan(8, 4), tokenSelector);
                    _log($"{channel}:{remote} mike activated: item={tokenItemCode} name={tokenCatalogItem.Name} selector={tokenSelector} mode={tokenCatalogItem.TokenMode} uses={tokenCatalogItem.TokenUseCount} remaining={mikeResult.Quantity} channelUses={mikeResult.ChannelUseCount} globalUses={mikeResult.GlobalUseCount}");
                    return BuildNativeFrame(frame, 0xC46E, mikeUsePayload, session);
                }

                await RefreshSessionCharacterAsync(session, token);
                if (!TryResolveGameInventoryIdentity(
                        session.Character,
                        tokenSelector,
                        out var selectedTokenItemCode)
                    || selectedTokenItemCode != tokenItemCode)
                {
                    _log($"{channel}:{remote} token use identity invalid: item={tokenItemCode} identity={tokenSelector}; inventory unchanged");
                    return BuildNativeFrame(
                        frame,
                        0xC46E,
                        BuildTokenUseResultPayload(false, tokenItemCode, tokenSelector),
                        session);
                }

                var useResult = await _database.ActivateTokenItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    tokenItemCode,
                    token);
                if (!useResult.Success)
                {
                    _log($"{channel}:{remote} token use rejected: item={tokenItemCode} identity={tokenSelector} quantity={useResult.Quantity} error={useResult.Error}");
                    return BuildNativeFrame(
                        frame,
                        0xC46E,
                        BuildTokenUseResultPayload(false, tokenItemCode, tokenSelector),
                        session);
                }
                await RefreshSessionCharacterAsync(session, token);
                var tokenUsePayload = new byte[12];
                // The C46E consumer branches on dword frame+8 == 1, then
                // removes one matching token and opens the category-47 UI
                // using frame+12/+16.
                BinaryPrimitives.WriteUInt32LittleEndian(tokenUsePayload.AsSpan(0, 4), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(tokenUsePayload.AsSpan(4, 4), tokenItemCode);
                BinaryPrimitives.WriteUInt32LittleEndian(tokenUsePayload.AsSpan(8, 4), tokenSelector);
                _log($"{channel}:{remote} token used: item={tokenItemCode} name={tokenCatalogItem.Name} identity={tokenSelector} remaining={useResult.Quantity} mysteryUses={useResult.MysteryKeyCount} goldenUses={useResult.GoldenKeyCount}");
                return BuildNativeFrame(frame, 0xC46E, tokenUsePayload, session);
            }

            case 0xC46B: // REQ_DEL_TOKENITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != TokenDeleteRequestPayloadLength)
                {
                    _log($"{channel}:{remote} token delete request length invalid: expected {TokenDeleteRequestPayloadLength}, actual {payload.Length}; inventory unchanged");
                    return null;
                }

                // C46B and C46D share the fixed item-code/inventory-identity fields.
                // C46C reports a dword result followed by those same fields.
                var tokenItemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var tokenIdentity = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                var responsePayload = new byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(responsePayload.AsSpan(4, 4), tokenItemCode);
                BinaryPrimitives.WriteUInt32LittleEndian(responsePayload.AsSpan(8, 4), tokenIdentity);
                await RefreshSessionCharacterAsync(session, token);
                if (!ShopCatalog.TryGet(tokenItemCode, out var tokenCatalogItem)
                    || tokenCatalogItem.Category != 47
                    || !TryResolveGameInventoryIdentity(session.Character, tokenIdentity, out var selectedTokenItemCode)
                    || selectedTokenItemCode != tokenItemCode)
                {
                    _log($"{channel}:{remote} token delete fields invalid: item={tokenItemCode} identity={tokenIdentity}; inventory unchanged");
                    return BuildNativeFrame(frame, 0xC46C, responsePayload, session);
                }

                var deleteResult = await _database.ConsumeTokenItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    tokenItemCode,
                    token);
                if (deleteResult.Success)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(responsePayload.AsSpan(0, 4), 1);
                    await RefreshSessionCharacterAsync(session, token);
                }
                _log($"{channel}:{remote} token delete: item={tokenItemCode} name={tokenCatalogItem.Name} identity={tokenIdentity} result={(deleteResult.Success ? "success" : "failure")} remaining={deleteResult.Quantity} error={deleteResult.Error}");
                return BuildNativeFrame(frame, 0xC46C, responsePayload, session);
            }

            case 0xC378: // REQ_MY_BOX_INFO
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != EmptyInventoryRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 箱子信息请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                var boxSkills = await _database.GetCharacterSkillsAsync(session.Character.Id, token);
                var boxInfo = BuildNativeFrame(
                    frame,
                    0xC379,
                    BuildBoxInfoPayloadWithSkills(session.Character, boxSkills),
                    session);
                var restoredPetAfterBox = BuildNativeFrame(frame, 0xC44C, BuildPetInventoryPayload(session.Character), session);
                return CombineNativeFrames(boxInfo, restoredPetAfterBox);

            case 0xC38D: // REQ_MOVE_MINIROOM -> ANS_MOVE_MINIROOM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != MiniRoomMoveRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 公寓进入包长度无效：期望 {MiniRoomMoveRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }

                var moveMode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                CharacterRecord? apartmentOwner;
                string requestedOwner;
                // Mode 1 is the "My Room" command. Mode 3 is emitted when
                // the player clicks the local house created by C36D; that
                // request intentionally carries no owner identity because
                // frame+84 == 100 already marked the house as this session's.
                if (moveMode is 1 or 3)
                {
                    await RefreshSessionCharacterAsync(session, token);
                    apartmentOwner = session.Character;
                    requestedOwner = session.Username;
                }
                else if (moveMode == 2
                    && TryDecodeGbkIdentity(
                        payload.AsSpan(MiniRoomMoveIdentityOffset, MiniRoomMoveIdentityLength),
                        out requestedOwner,
                        out _))
                {
                    if (SessionIdentityMatches(requestedOwner, session))
                    {
                        await RefreshSessionCharacterAsync(session, token);
                        apartmentOwner = session.Character;
                    }
                    else
                    {
                        var ownerAccountId = long.TryParse(requestedOwner, out var numericAccountId)
                            ? numericAccountId
                            : await _database.GetAccountIdByUsernameAsync(requestedOwner, token) ?? 0;
                        apartmentOwner = ownerAccountId > 0
                            ? await _database.GetCharacterAsync(ownerAccountId, token)
                            : null;
                    }
                }
                else
                {
                    _log($"{channel}:{remote} 公寓进入模式或房主标识无效：mode={moveMode}；返回原版失败状态");
                    return BuildNativeFrame(
                        frame,
                        0xC38E,
                        BuildMiniRoomMovePayload(null, false),
                        session);
                }

                if (apartmentOwner is null)
                {
                    _log($"{channel}:{remote} 公寓房主不存在：mode={moveMode} owner={requestedOwner}；返回原版失败状态");
                    return BuildNativeFrame(
                        frame,
                        0xC38E,
                        BuildMiniRoomMovePayload(null, false),
                        session);
                }

                LeaveTradeRoomScene(session, "apartment enter");
                LeaveTownScene(session, "apartment enter");
                LeaveVillageShopScene(session, "apartment enter");
                LeaveApartmentScene(session, "apartment room change");
                session.ApartmentOwnerCharacterId = apartmentOwner.Id;
                var apartmentRecovery = await ApplyNonCombatHealthRecoveryAsync(
                    session,
                    HealthRecoveryScene.Apartment,
                    token);
                if (apartmentRecovery.Changed)
                    _log($"{channel}:{remote} apartment recovery step: hp={apartmentRecovery.CurrentHp}/{session.Character!.MaxHp} mp={apartmentRecovery.CurrentMp}/{session.Character.MaxMp} restored=({apartmentRecovery.HpRestored},{apartmentRecovery.MpRestored})");
                var apartmentPlacements = await _database.GetApartmentPlacementsAsync(apartmentOwner.Id, token);
                _log($"{channel}:{remote} 进入公寓：mode={moveMode} owner={requestedOwner} character={apartmentOwner.Name} objects={apartmentPlacements.Count(item => item.InteriorType >= 2)}；返回完整 C38E 私人房间结构");
                return BuildNativeFrame(
                    frame,
                    0xC38E,
                    BuildMiniRoomMovePayload(apartmentOwner, true, apartmentPlacements),
                    session);
            }

            case 0xC38F: // REQ_MINIROOM_USER_INFO -> ANS_MINIROOM_USER_INFO
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != MiniRoomUserInfoRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 公寓角色信息请求长度无效：期望 {MiniRoomUserInfoRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                var roomX = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var roomY = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                session.LastReportedPositionX = roomX;
                session.LastReportedPositionY = roomY;
                session.Character!.PositionX = roomX;
                session.Character.PositionY = roomY;
                var apartmentRefreshRecovery = await ApplyNonCombatHealthRecoveryAsync(
                    session,
                    HealthRecoveryScene.Apartment,
                    token);
                if (apartmentRefreshRecovery.Changed)
                    _log($"{channel}:{remote} apartment refresh recovery step: hp={apartmentRefreshRecovery.CurrentHp}/{session.Character!.MaxHp} mp={apartmentRefreshRecovery.CurrentMp}/{session.Character.MaxMp} restored=({apartmentRefreshRecovery.HpRestored},{apartmentRefreshRecovery.MpRestored})");
                var apartmentUserPayload = BuildMiniRoomUserInfoPayload(session.Character, roomX, roomY);
                QueueApartmentEntitySnapshots(session, apartmentUserPayload);
                _log($"{channel}:{remote} 返回公寓角色信息：character={session.Character?.Name} position=({roomX},{roomY})");
                return BuildNativeFrame(
                    frame,
                    0xC390,
                    apartmentUserPayload,
                    session);
            }

            case 0xC392: // REQ_MINIROOM_OBJECT_INFO -> ANS_MINIROOM_OBJECT_INFO
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != MiniRoomObjectInfoRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 公寓对象请求长度无效：期望 {MiniRoomObjectInfoRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                var apartmentOwnerId = session.ApartmentOwnerCharacterId > 0
                    ? session.ApartmentOwnerCharacterId
                    : session.Character.Id;
                var roomPlacements = await _database.GetApartmentPlacementsAsync(apartmentOwnerId, token);
                var roomObjectPayload = BuildMiniRoomObjectInfoPayload(roomPlacements);
                _log($"{channel}:{remote} 杩斿洖鍏瘬鍔ㄦ€佸璞″垪琛細requestId={BinaryPrimitives.ReadUInt32LittleEndian(payload)} ownerCharacterId={apartmentOwnerId} count={BinaryPrimitives.ReadUInt16LittleEndian(roomObjectPayload.AsSpan(0, 2))}");
                return BuildNativeFrame(
                    frame,
                    0xC393,
                    roomObjectPayload,
                    session);

            case 0xC423: // REQ_MY_INTERIOR_INFO -> ANS_MY_INTERIOR_INFO
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != ApartmentInteriorInfoRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 公寓布局请求长度无效：期望 {ApartmentInteriorInfoRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                var placements = await _database.GetApartmentPlacementsAsync(session.Character.Id, token);
                var layoutPayload = BuildApartmentInteriorInfoPayload(placements);
                _log($"{channel}:{remote} 返回公寓布局：requestInfo={BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2))} requestIndex={BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2))} objects={BinaryPrimitives.ReadUInt16LittleEndian(layoutPayload.AsSpan(2, 2))} floor={placements.Count(item => item.InteriorType == 0)} wall={placements.Count(item => item.InteriorType == 1)}");
                return BuildNativeFrame(frame, 0xC424, layoutPayload, session);
            }

            case 0xC405: // REQ_INTERIORITEM_LIST -> ANS_INTERIORITEM_LIST
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != InteriorCatalogRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 装修分类列表请求长度无效：期望 {InteriorCatalogRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                var category = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var page = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var placements = await _database.GetApartmentPlacementsAsync(session.Character!.Id, token);
                var catalogPayload = BuildInteriorCatalogPayload(category, page, session.Character, placements);
                _log($"{channel}:{remote} 返回装修分类列表：category={category} page={page} count={BinaryPrimitives.ReadUInt16LittleEndian(catalogPayload.AsSpan(2, 2))}");
                return BuildNativeFrame(frame, 0xC406, catalogPayload, session);
            }

            case 0xC398: // REQ_RECOMMEND_COUNT -> ANS_RECOMMEND_COUNT
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != ApartmentRecommendCountRequestPayloadLength)
                {
                    _log($"{channel}:{remote} apartment recommend-count request length invalid: expected {ApartmentRecommendCountRequestPayloadLength}, actual {payload.Length}; no response");
                    return null;
                }
                // No recommendation records exist yet, so the persisted room count is zero.
                return BuildNativeFrame(frame, 0xC399, BuildApartmentRecommendCountPayload(0), session);

            case 0xC417: // REQ_MY_INTERIORITEM_WISHLIST -> ANS_MY_INTERIORITEM_WISHLIST
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != InteriorWishlistRequestPayloadLength)
                {
                    _log($"{channel}:{remote} interior wishlist request length invalid: expected {InteriorWishlistRequestPayloadLength}, actual {payload.Length}; no response");
                    return null;
                }
                var wishlist = await _database.GetInteriorWishlistAsync(session.Character.Id, token);
                _log($"{channel}:{remote} returning interior wishlist: characterId={session.Character.Id} count={wishlist.Count}");
                return BuildNativeFrame(frame, 0xC418, BuildInteriorWishlistPayload(wishlist), session);
            }

            case 0xC419: // REQ_CHOICE_WISH_INTERIORITEM -> ANS_CHOICE_WISH_INTERIORITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != InteriorWishlistChoiceRequestPayloadLength)
                {
                    _log($"{channel}:{remote} interior wishlist choice length invalid: expected {InteriorWishlistChoiceRequestPayloadLength}, actual {payload.Length}; returning failure");
                    return BuildNativeFrame(frame, 0xC41A, BuildInteriorWishlistChoiceResultPayload(30), session);
                }

                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                if (!ShopCatalog.TryGet(itemCode, out var item)
                    || item.Category != 11
                    || item.Section != InventorySection.Furniture)
                {
                    _log($"{channel}:{remote} rejected non-catalog interior wishlist item: item={itemCode}");
                    return BuildNativeFrame(frame, 0xC41A, BuildInteriorWishlistChoiceResultPayload(30), session);
                }

                var added = await _database.AddInteriorWishlistItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    InteriorWishlistCapacity,
                    token);
                // C41A client branches: 10 capacity reached, 20 saved,
                // 30 duplicate or rejected item.
                var resultCode = added.CapacityReached ? 10u : added.Success ? 20u : 30u;
                _log($"{channel}:{remote} interior wishlist choice: characterId={session.Character.Id} item={itemCode} result={resultCode} error={added.Error}");
                return BuildNativeFrame(frame, 0xC41A, BuildInteriorWishlistChoiceResultPayload(resultCode), session);
            }

            case 0xC41B: // REQ_DEL_WISH_INTERIORITEM -> ANS_DEL_WISH_INTERIORITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != InteriorWishlistDeleteRequestPayloadLength)
                {
                    _log($"{channel}:{remote} interior wishlist delete length invalid: expected {InteriorWishlistDeleteRequestPayloadLength}, actual {payload.Length}; returning failure");
                    return BuildNativeFrame(frame, 0xC41C, BuildInteriorWishlistDeleteResultPayload(false), session);
                }

                // The final DWORD is uninitialized client stack data and changes
                // between clicks. The stable fields are item code, zero and quantity.
                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var reserved = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
                var quantity = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                if (reserved != 0 || quantity == 0)
                {
                    _log($"{channel}:{remote} interior wishlist delete fields invalid: item={itemCode} reserved={reserved} quantity={quantity}; returning failure");
                    return BuildNativeFrame(frame, 0xC41C, BuildInteriorWishlistDeleteResultPayload(false), session);
                }

                var deleted = await _database.DeleteInteriorWishlistItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    token);
                _log($"{channel}:{remote} interior wishlist delete: characterId={session.Character.Id} item={itemCode} quantity={quantity} result={(deleted ? "success" : "failure")}");
                return BuildNativeFrame(frame, 0xC41C, BuildInteriorWishlistDeleteResultPayload(deleted), session);
            }

            case 0xC42D: // REQ_MY_SHOPITEM_STATE -> ANS_MY_SHOPITEM_STATE
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != ShopOwnedStateRequestPayloadLength)
                {
                    _log($"{channel}:{remote} shop owned-state request length invalid: expected {ShopOwnedStateRequestPayloadLength}, actual {payload.Length}; no response");
                    return null;
                }

                await RefreshSessionCharacterAsync(session, token);
                var requestedLimit = Math.Min(
                    (int)BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2)),
                    ShopOwnedStateCapacity);
                var itemCodes = session.Character?.Items
                    .Where(entry => entry.Quantity > 0
                        && ShopCatalog.TryGet(entry.ItemCode, out var item)
                        && IsSupportedShopWishlistCategory(item.Category))
                    .Select(entry => entry.ItemCode)
                    .Distinct()
                    .Take(requestedLimit)
                    .ToArray() ?? [];
                _log($"{channel}:{remote} returning shop owned-item state: characterId={session.Character?.Id ?? 0} requested={requestedLimit} count={itemCodes.Length}");
                return BuildNativeFrame(frame, 0xC42E, BuildShopOwnedStatePayload(itemCodes), session);
            }

            case 0xC437: // REQ_MY_AVATAITEM_WISHLIST -> ANS_MY_AVATAITEM_WISHLIST
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != ShopWishlistRequestPayloadLength)
                {
                    _log($"{channel}:{remote} shop wishlist request length invalid: expected {ShopWishlistRequestPayloadLength}, actual {payload.Length}; no response");
                    return null;
                }

                var wishlist = await _database.GetShopWishlistAsync(session.Character.Id, token);
                _log($"{channel}:{remote} returning shop wishlist: characterId={session.Character.Id} count={wishlist.Count}");
                return BuildNativeFrame(frame, 0xC438, BuildShopWishlistPayload(wishlist), session);
            }

            case 0xC439: // REQ_CHOICE_WISH_AVATAITEM -> ANS_CHOICE_WISH_AVATAITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != ShopWishlistChoiceRequestPayloadLength)
                {
                    _log($"{channel}:{remote} shop wishlist choice length invalid: expected {ShopWishlistChoiceRequestPayloadLength}, actual {payload.Length}; returning rejection");
                    return BuildNativeFrame(frame, 0xC43A, BuildShopWishlistChoiceResultPayload(false), session);
                }

                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                if (!ShopCatalog.TryGet(itemCode, out var item)
                    || !IsSupportedShopWishlistCategory(item.Category))
                {
                    _log($"{channel}:{remote} rejected non-catalog shop wishlist item: item={itemCode}");
                    return BuildNativeFrame(frame, 0xC43A, BuildShopWishlistChoiceResultPayload(false), session);
                }

                var added = await _database.AddShopWishlistItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    ShopWishlistCapacity,
                    token);
                _log($"{channel}:{remote} shop wishlist choice: characterId={session.Character.Id} item={itemCode} category={item.Category} result={(added.Success ? 200 : 100)} wishlistId={added.WishlistId} duplicate={added.AlreadyExists} capacity={added.CapacityReached} error={added.Error}");
                return BuildNativeFrame(frame, 0xC43A, BuildShopWishlistChoiceResultPayload(added.Success), session);
            }

            case 0xC43B: // REQ_DEL_WISH_AVATAITEM -> ANS_DEL_WISH_AVATAITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != ShopWishlistDeleteRequestPayloadLength)
                {
                    _log($"{channel}:{remote} shop wishlist delete length invalid: expected {ShopWishlistDeleteRequestPayloadLength}, actual {payload.Length}; returning failure");
                    return BuildNativeFrame(frame, 0xC43C, BuildShopWishlistDeleteResultPayload(false), session);
                }

                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var wishlistId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                var validItem = ShopCatalog.TryGet(itemCode, out var item)
                    && IsSupportedShopWishlistCategory(item.Category);
                var deleted = validItem && await _database.DeleteShopWishlistItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    wishlistId,
                    token);
                _log($"{channel}:{remote} shop wishlist delete: characterId={session.Character.Id} item={itemCode} wishlistId={wishlistId} result={(deleted ? 200 : 100)}");
                return BuildNativeFrame(frame, 0xC43C, BuildShopWishlistDeleteResultPayload(deleted), session);
            }

            case 0xC3D4: // NaNa shop wishlist load -> C3D5
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != NanaWishlistRequestPayloadLength)
                {
                    _log($"{channel}:{remote} NaNa wishlist request length invalid: expected {NanaWishlistRequestPayloadLength}, actual {payload.Length}; no response");
                    return null;
                }

                var wishlist = await _database.GetNanaWishlistAsync(session.Character.Id, token);
                _log($"{channel}:{remote} returning NaNa wishlist: characterId={session.Character.Id} count={wishlist.Count}");
                return BuildNativeFrame(frame, 0xC3D5, BuildNanaWishlistPayload(wishlist), session);
            }

            case 0xC3D6: // NaNa shop wishlist add -> C3D7
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != NanaWishlistChoiceRequestPayloadLength)
                {
                    _log($"{channel}:{remote} NaNa wishlist add length invalid: expected {NanaWishlistChoiceRequestPayloadLength}, actual {payload.Length}; returning rejection");
                    return BuildNativeFrame(frame, 0xC3D7, BuildNanaWishlistChoiceResultPayload(30), session);
                }

                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                var added = await _database.AddNanaWishlistItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    NanaWishlistCapacity,
                    token);
                var resultCode = added.Success ? 10u : added.CapacityReached ? 20u : 30u;
                _log($"{channel}:{remote} NaNa wishlist add: characterId={session.Character.Id} item={itemCode} result={resultCode} duplicate={added.AlreadyExists} capacity={added.CapacityReached} error={added.Error}");
                return BuildNativeFrame(frame, 0xC3D7, BuildNanaWishlistChoiceResultPayload(resultCode), session);
            }

            case 0xC3D8: // NaNa shop wishlist delete -> C3D9
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != NanaWishlistDeleteRequestPayloadLength)
                {
                    _log($"{channel}:{remote} NaNa wishlist delete length invalid: expected {NanaWishlistDeleteRequestPayloadLength}, actual {payload.Length}; returning failure");
                    return BuildNativeFrame(frame, 0xC3D9, BuildNanaWishlistDeleteResultPayload(false), session);
                }

                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                var deleted = await _database.DeleteNanaWishlistItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    token);
                _log($"{channel}:{remote} NaNa wishlist delete: characterId={session.Character.Id} item={itemCode} result={(deleted ? 200 : 100)}");
                return BuildNativeFrame(frame, 0xC3D9, BuildNanaWishlistDeleteResultPayload(deleted), session);
            }

            case 0xC411: // REQ_CHANGE_INTERIORITEM -> ANS_CHANGE_INTERIORITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != InteriorChangeRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 装修保存请求长度无效：期望 {InteriorChangeRequestPayloadLength}，实际 {payload.Length}；返回失败");
                    return BuildNativeFrame(frame, 0xC412, BuildInteriorChangeResultPayload(false), session);
                }

                await RefreshSessionCharacterAsync(session, token);
                var inventory = GetInteriorItemCodes(session.Character!);
                var takeOnCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(InteriorChangeTakeOnCountOffset, 2));
                var takeOffCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(InteriorChangeTakeOffCountOffset, 2));
                if (takeOnCount > ApartmentInteriorObjectCapacity || takeOffCount > ApartmentInteriorObjectCapacity)
                {
                    _log($"{channel}:{remote} 装修保存计数越界：takeOn={takeOnCount} takeOff={takeOffCount}；返回失败");
                    return BuildNativeFrame(frame, 0xC412, BuildInteriorChangeResultPayload(false), session);
                }

                var takeOn = new List<ApartmentPlacementRecord>(takeOnCount);
                var valid = true;
                for (var index = 0; index < takeOnCount; index++)
                {
                    var recordOffset = index * InteriorChangeTakeOnRecordLength;
                    var apartmentSlot = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(recordOffset, 2));
                    if (apartmentSlot >= inventory.Length || apartmentSlot >= ApartmentInteriorObjectCapacity)
                    {
                        valid = false;
                        break;
                    }
                    var itemCode = inventory[apartmentSlot];
                    if (!ShopCatalog.TryGet(itemCode, out var catalogItem)
                        || catalogItem.Section != InventorySection.Furniture
                        || catalogItem.InteriorType > 4)
                    {
                        valid = false;
                        break;
                    }
                    takeOn.Add(new ApartmentPlacementRecord
                    {
                        SlotIndex = checked((byte)apartmentSlot),
                        ItemCode = itemCode,
                        X = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(recordOffset + 2, 2)),
                        Y = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(recordOffset + 4, 2)),
                        Layer = payload[recordOffset + 6],
                        Mirror = payload[recordOffset + 7],
                        InteriorType = catalogItem.InteriorType
                    });
                }

                var takeOff = payload
                    .AsSpan(InteriorChangeTakeOffOffset, takeOffCount)
                    .ToArray();
                if (takeOff.Any(slotIndex => slotIndex >= ApartmentInteriorObjectCapacity))
                    valid = false;

                var saved = valid && await _database.ApplyApartmentChangesAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    takeOn,
                    takeOff,
                    token);
                _log($"{channel}:{remote} 装修保存：takeOn={takeOnCount} takeOff={takeOffCount} parsed={valid} result={(saved ? "success" : "failure")}");
                if (saved)
                {
                    AccountStateChanged?.Invoke();
                    if (session.ApartmentOwnerCharacterId == session.Character.Id)
                    {
                        var refreshedPlacements = await _database.GetApartmentPlacementsAsync(
                            session.Character.Id,
                            token);
                        QueueApartmentBroadcast(
                            session,
                            0xC393,
                            BuildMiniRoomObjectInfoPayload(refreshedPlacements),
                            "apartment furniture refresh");
                    }
                }
                return BuildNativeFrame(frame, 0xC412, BuildInteriorChangeResultPayload(saved), session);
            }

            case 0xC3FB: // REQ_UPDATE_DDAKGI_GUIDESTEP
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != DdakgiGuideStepPayloadLength)
                {
                    _log($"{channel}:{remote} 卡片引导步骤请求长度无效：期望 {DdakgiGuideStepPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                var currentCardGuideStep = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var requestedCardGuideStep = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var validCardGuideAdvance = currentCardGuideStep <= 3
                    && requestedCardGuideStep is >= 1 and <= 3
                    && requestedCardGuideStep >= currentCardGuideStep;
                byte? storedCardGuideStep = null;
                if (validCardGuideAdvance)
                {
                    storedCardGuideStep = await _database.AdvanceCardGuideStepAsync(
                        session.AccountId,
                        session.Character.Id,
                        session.SessionId,
                        checked((byte)currentCardGuideStep),
                        checked((byte)requestedCardGuideStep),
                        token);
                    if (storedCardGuideStep.HasValue)
                        session.Character.CardGuideStep = storedCardGuideStep.Value;
                }
                var cardGuideUpdated = storedCardGuideStep.HasValue;
                _log($"{channel}:{remote} card guide advance: current={currentCardGuideStep} requested={requestedCardGuideStep} stored={storedCardGuideStep?.ToString() ?? "none"} result={(cardGuideUpdated ? "success" : "failure")}");
                // sub_844D10 sets its wait flag after sending C3FB, while
                // sub_845180 clears it on C3FC. A loopback response in the
                // same UI tick can otherwise clear the flag before it is set.
                await Task.Delay(DdakgiGuideStepResponseDelay, token);
                return BuildNativeFrame(frame, 0xC3FC, BuildDdakgiGuideStepResultPayload(cardGuideUpdated), session);

            case 0xC3E7: // REQ_MY_DDAKGI_LIST
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != CardListRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 卡片列表请求长度无效：期望 {CardListRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                var ownedCards = await _database.GetCharacterCardsAsync(session.Character.Id, token);
                _log($"{channel}:{remote} 返回卡片册：mode={BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2))} category={payload[2]} page={payload[3]} owned={ownedCards.Count}");
                var learnedSkills = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2)) == 40
                    ? await _database.GetCharacterSkillsAsync(session.Character.Id, token)
                    : [];
                return BuildNativeFrame(
                    frame,
                    0xC3E8,
                    BuildCardListPayload(payload, ownedCards, session.Character, learnedSkills),
                    session);

            case 0xC3FF: // REQ_UPGRADE_SKILL_DDAKGI -> ANS_UPGRADE_SKILL_DDAKGI
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;

                await RefreshSessionCharacterAsync(session, token);

                var requestedSkillCode = payload.Length == SkillUpgradeRequestPayloadLength
                    ? BinaryPrimitives.ReadUInt32LittleEndian(payload)
                    : 0u;
                if (payload.Length != SkillUpgradeRequestPayloadLength)
                {
                    _log($"{channel}:{remote} skill upgrade request length invalid: expected {SkillUpgradeRequestPayloadLength}, actual {payload.Length}");
                    return BuildNativeFrame(frame, 0xC400,
                        BuildSkillUpgradeResultPayload(1, session.Character.SkillPoints, requestedSkillCode), session);
                }

                var upgrade = await _database.UpgradeCharacterSkillAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    requestedSkillCode,
                    token);
                if (upgrade.Success)
                    await RefreshSessionCharacterAsync(session, token);
                var remainingSkillPoints = upgrade.Success
                    ? upgrade.RemainingSkillPoints
                    : session.Character?.SkillPoints ?? upgrade.RemainingSkillPoints;
                _log($"{channel}:{remote} skill upgrade: skill={requestedSkillCode} result={(upgrade.Success ? "success" : "failure")} grade={upgrade.Grade} remainingSP={remainingSkillPoints} error={upgrade.Error}");
                return BuildNativeFrame(frame, 0xC400,
                    BuildSkillUpgradeResultPayload(upgrade.Success ? (ushort)0 : (ushort)1, remainingSkillPoints, requestedSkillCode),
                    session);
            }

            case 0xC401: // REQ_USE_SKILLSLOT -> ANS_USE_SKILLSLOT
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;

                if (payload.Length != SkillSlotRequestPayloadLength)
                {
                    _log($"{channel}:{remote} skill slot request length invalid: expected {SkillSlotRequestPayloadLength}, actual {payload.Length}");
                    return BuildNativeFrame(frame, 0xC402,
                        BuildSkillSlotResultPayload(1, 0, 0, 0, 0), session);
                }

                var requestedSkill0 = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var requestedSkill1 = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                var saved = await _database.SaveCharacterSkillSlotsAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    requestedSkill0,
                    requestedSkill1,
                    token);
                if (saved.Success)
                    await RefreshSessionCharacterAsync(session, token);
                _log($"{channel}:{remote} skill slots: requested={requestedSkill0}/{requestedSkill1} stored={saved.Skill0}:{saved.Grade0}/{saved.Skill1}:{saved.Grade1} result={(saved.Success ? "success" : "failure")} error={saved.Error}");
                return BuildNativeFrame(frame, 0xC402,
                    BuildSkillSlotResultPayload(saved.Success ? (ushort)0 : (ushort)1,
                        saved.Skill0, saved.Grade0, saved.Skill1, saved.Grade1), session);
            }

            case 0xC480: // REQ_USE_INVENTORY_ADD_ITEM -> ANS_USE_INVENTORY_ADD_ITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;

                var requestControl = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0, 2));
                if (session.LastSkillSlotExpansionRequestControl == requestControl
                    && session.LastSkillSlotExpansionResultPayload is { } cachedResult
                    && DateTime.UtcNow - session.LastSkillSlotExpansionRequestUtc <= TimeSpan.FromSeconds(5))
                {
                    _log($"{channel}:{remote} replayed C480 transport frame; returning cached C481 without consuming another ticket");
                    return BuildNativeFrame(frame, 0xC481, cachedResult, session);
                }

                var requestParsed = TryParseInventoryExpansionRequest(
                    payload,
                    out var requestedType,
                    out var requestReserved);
                uint itemCode = 0;
                byte inventorySlot = 0;
                byte[] resultPayload;
                if (!requestParsed || requestedType > 6)
                {
                    _log($"{channel}:{remote} inventory expansion request invalid: payload={payload.Length} type={requestedType} reserved={requestReserved}; inventory unchanged");
                    resultPayload = BuildSkillSlotExpansionResultPayload(1, requestedType, inventorySlot, itemCode, 0);
                }
                else
                {
                    await RefreshSessionCharacterAsync(session, token);
                    if (session.Character is null
                        || !TryFindInventoryExpansionTicket(
                            session.Character,
                            requestedType,
                            out var ticket,
                            out inventorySlot))
                    {
                        _log($"{channel}:{remote} inventory expansion rejected: no type-{requestedType} ticket in the game-item inventory");
                        resultPayload = BuildSkillSlotExpansionResultPayload(1, requestedType, 0, 0, 0);
                    }
                    else
                    {
                        itemCode = ticket.ItemCode;
                        var use = await _database.UseInventoryExpansionAsync(
                            session.AccountId,
                            session.Character.Id,
                            session.SessionId,
                            itemCode,
                            DateTime.Now,
                            token);
                        if (use.Success)
                            await RefreshSessionCharacterAsync(session, token);
                        _log($"{channel}:{remote} inventory expansion: type={requestedType} item={itemCode} slot={inventorySlot} durationDays={ticket.DurationDays} result={(use.Success ? "success" : "failure")} expires={use.Expiration} remaining={use.RemainingQuantity} error={use.Error}");
                        resultPayload = BuildSkillSlotExpansionResultPayload(
                            use.Success ? (byte)0 : (byte)1,
                            requestedType,
                            inventorySlot,
                            itemCode,
                            use.Success ? use.Expiration : 0);
                    }
                }

                session.LastSkillSlotExpansionRequestControl = requestControl;
                session.LastSkillSlotExpansionRequestUtc = DateTime.UtcNow;
                session.LastSkillSlotExpansionResultPayload = resultPayload.ToArray();
                return BuildNativeFrame(frame, 0xC481, resultPayload, session);
            }

            case 0xC3F3: // REQ_SELL_DDAKGI -> ANS_SELL_DDAKGI
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;

                if (payload.Length != CardSellRequestPayloadLength)
                {
                    _log($"{channel}:{remote} card sale request length invalid: expected {CardSellRequestPayloadLength}, actual {payload.Length}; returning C3F4 failure");
                    return BuildNativeFrame(frame, 0xC3F4, new byte[4], session);
                }

                var cardCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var chapter = payload[4];
                var page = payload[5];
                var index = payload[6];
                var quantity = payload[7];
                var sale = await _database.SellCharacterCardAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    cardCode,
                    chapter,
                    page,
                    index,
                    quantity,
                    token);
                if (sale.Success)
                    await RefreshSessionCharacterAsync(session, token);

                var saleResultPayload = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(saleResultPayload, sale.Success ? 1u : 0u);
                _log($"{channel}:{remote} card sale: card={cardCode} chapter={chapter} page={page} index={index} quantity={quantity} result={(sale.Success ? "success" : "failure")} remaining={sale.Quantity} hans={sale.Hans} error={sale.Error}");
                return BuildNativeFrame(frame, 0xC3F4, saleResultPayload, session);
            }

            case 0xC3E9: // REQ_MY_SUMMON_INFO
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != CardSummonRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 卡片召唤状态请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                // The retail card-book dispatcher consumes C3EA here and then waits
                // for C3E8. C46A belongs exclusively to the C469 inventory request;
                // inserting it into this response leaves C46A at the queue head and
                // prevents the card-book dispatcher from reaching C3E8.
                return BuildNativeFrame(
                    frame,
                    0xC3EA,
                    BuildCardSummonPayload(session.Character),
                    session);

            case 0xC3ED: // REQ_MAKE_UNION_DDAKGI
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;

                if (!TryParseCardSynthesisRequest(payload, out var unionType, out var padding, out var recipeToken)
                    || unionType != CardUnionType
                    || !CardSynthesisCatalog.TryGet(recipeToken, out var recipe))
                {
                    _log($"{channel}:{remote} card synthesis request invalid: length={payload.Length} type={unionType} padding=0x{padding:X4} token={recipeToken}; returning C3EE failure");
                    return BuildNativeFrame(frame, 0xC3EE, BuildCardSynthesisResultPayload(false, 0), session);
                }

                // C3ED/28 carries type at frame+8 and recipe token at frame+0C.
                // frame+0A is uninitialized client padding and frame+10..+1B are
                // not material/card selectors, so neither participates in policy.
                var synthesis = await _database.SynthesizeCardItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    recipeToken,
                    token);
                if (synthesis.Success)
                    await RefreshSessionCharacterAsync(session, token);
                _log($"{channel}:{remote} card synthesis: token={recipeToken} inputs={recipe.Input0}/{recipe.Input1}/{recipe.Input2} output={recipe.Output} key={synthesis.KeyKind} keyRemaining={synthesis.KeyUseCount} rewardQuantity={synthesis.RewardQuantity} padding=0x{padding:X4} result={(synthesis.Success ? "success" : "failure")} error={synthesis.Error}");
                return BuildNativeFrame(
                    frame,
                    0xC3EE,
                    BuildCardSynthesisResultPayload(synthesis.Success, synthesis.ItemCode),
                    session);
            }

            case 0xC3EF: // REQ_MAKE_UNION_DDAKGI_FINISH
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != 0)
                {
                    _log($"{channel}:{remote} card synthesis finish length invalid: expected 0, actual {payload.Length}; no response");
                    return null;
                }
                return BuildNativeFrame(frame, 0xC3F0, BuildCardSynthesisFinishPayload(), session);

            case 0xC37A: // REQ_MOVE_SHOP
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != ShopMoveRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 商城切换请求长度无效：期望 {ShopMoveRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                await RefreshSessionCharacterAsync(session, token);
                return BuildNativeFrame(frame, 0xC37B, BuildShopMovePayload(session.Character), session);

            case 0xC3AB: // REQ_MOVE_SHOP -> ANS_MOVE_SHOP for physical village shops
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != VillageShopEnterRequestPayloadLength)
                {
                    _log($"{channel}:{remote} village shop enter length invalid: expected {VillageShopEnterRequestPayloadLength}, actual {payload.Length}; no response");
                    return null;
                }

                var shopCodeValue = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                var validShopCode = shopCodeValue is 110u or 120u or 130u or 140u or 150u;
                var shopCode = validShopCode ? checked((byte)shopCodeValue) : (byte)0;
                if (validShopCode)
                {
                    LeaveTradeRoomScene(session, "village shop enter");
                    LeaveTownScene(session, "village shop enter");
                    LeaveApartmentScene(session, "village shop enter");
                    LeaveVillageShopScene(session, "village shop change");
                    session.VillageShopCode = shopCode;
                }
                _log($"{channel}:{remote} village shop enter: requested={shopCodeValue} result={(validShopCode ? 100 : 0)}");
                return BuildNativeFrame(
                    frame,
                    0xC3AC,
                    BuildVillageShopEnterResultPayload(shopCode, validShopCode),
                    session);
            }

            case 0xC3AD: // REQ_SHOP_USER_INFO
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != ShopUserInfoRequestPayloadLength)
                {
                    _log($"{channel}:{remote} shop user-info request length invalid: expected {ShopUserInfoRequestPayloadLength}, actual {payload.Length}; no response");
                    return null;
                }

                var positionX = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var positionY = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                session.LastReportedPositionX = positionX;
                session.LastReportedPositionY = positionY;
                session.Character.PositionX = positionX;
                session.Character.PositionY = positionY;
                var shopUserPayload = BuildShopUserInfoPayload(session.Character);
                QueueVillageShopEntitySnapshots(session, shopUserPayload);
                _log($"{channel}:{remote} shop user-info synchronized: shop={session.VillageShopCode} position=({positionX},{positionY}) entity={session.Character.Id} name={session.Character.Name}");
                return BuildNativeFrame(
                    frame,
                    0xC3AE,
                    shopUserPayload,
                    session);
            }

            case 0xC451: // REQ_MY_CHARGE_PETITEM_LIST
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != PetChargeListRequestPayloadLength)
                {
                    _log($"{channel}:{remote} pet-charge list request length invalid: expected {PetChargeListRequestPayloadLength}, actual {payload.Length}; no response");
                    return null;
                }

                await RefreshSessionCharacterAsync(session, token);
                var chargeListPayload = BuildPetChargeListPayload(session.Character);
                var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(chargeListPayload.AsSpan(0, 4));
                _log($"{channel}:{remote} returning pet-charge list: count={itemCount} hans={session.Character?.Hans ?? 0} cash={session.Character?.Cash ?? 0}");
                return BuildNativeFrame(frame, 0xC452, chargeListPayload, session);
            }

            case 0xC453: // REQ_CHARGE_PETITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != PetChargeRequestPayloadLength)
                {
                    _log($"{channel}:{remote} pet-charge request length invalid: expected {PetChargeRequestPayloadLength}, actual {payload.Length}; returning failure");
                    return BuildNativeFrame(frame, 0xC454, BuildPetChargeResultPayload(false), session);
                }

                await RefreshSessionCharacterAsync(session, token);
                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var slot = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2));
                var durability = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(14, 2));
                var ownedPetCodes = GetOwnedPetItemCodes(session.Character).Distinct().Take(PetChargeCapacity).ToArray();
                ShopCatalog.TryGet(itemCode, out var petItem);
                var valid = slot < ownedPetCodes.Length
                    && ownedPetCodes[slot] == itemCode
                    && durability >= 0
                    && petItem is not null
                    && petItem.Section == InventorySection.Pet
                    && petItem.PetMaxDurability > 0
                    && petItem.PetChargeUnitPrice > 0;
                if (!valid)
                {
                    _log($"{channel}:{remote} pet-charge rejected: item={itemCode} slot={slot} durability={durability} reason=invalid-or-permanent-pet");
                    return BuildNativeFrame(frame, 0xC454, BuildPetChargeResultPayload(false), session);
                }

                var chargePet = petItem!;
                var result = await _database.ChargePetItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    durability,
                    chargePet.PetMaxDurability,
                    chargePet.PetChargeUnitPrice,
                    token);
                if (result.Success)
                    await RefreshSessionCharacterAsync(session, token);
                _log($"{channel}:{remote} pet-charge {(result.Success ? "completed" : "rejected")}: item={itemCode} slot={slot} durability={durability}->{result.Durability} max={chargePet.PetMaxDurability} unit={chargePet.PetChargeUnitPrice} cost={result.Cost} hans={result.Hans} reason={result.Error}");
                return BuildNativeFrame(frame, 0xC454, BuildPetChargeResultPayload(result.Success), session);
            }

            case 0xC431: // REQ_BUY_ITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != ShopPurchaseRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 商城购买请求长度无效：期望 {ShopPurchaseRequestPayloadLength}，实际 {payload.Length}；返回失败结果");
                    return BuildNativeFrame(frame, 0xC432, BuildShopPurchaseResultPayload(40, 0, 0, 0, 0, session.Character.Cash, session.Character.Hans), session);
                }

                var paymentMode = payload[0];
                var category = payload[1];
                var quantity = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));

                // Category and prices come from the exact client resource row
                // for clothing, interior, pet and game items.
                var catalogItemFound = ShopCatalog.TryGet(category, itemCode, out var catalogItem);
                if (!IsNativeShopPaymentMode(paymentMode)
                    || quantity == 0
                    || !catalogItemFound
                    || !catalogItem.IsPurchasable)
                {
                    await RefreshSessionCharacterAsync(session, token);
                    _log($"{channel}:{remote} 商城购买请求不在已验证目录：mode={paymentMode} category={category} quantity={quantity} item={itemCode}；返回协议失败结果");
                    return BuildNativeFrame(frame, 0xC432, BuildShopPurchaseResultPayload(40, paymentMode, itemCode, 0, 0, session.Character?.Cash ?? 0, session.Character?.Hans ?? 0), session);
                }

                var purchase = await _database.PurchaseShopItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    paymentMode,
                    itemCode,
                    quantity,
                    catalogItem.PurchasePrice,
                    catalogItem.PaysWithCash,
                    token);
                await RefreshSessionCharacterAsync(session, token);
                var resultCode = purchase.Success
                    ? (byte)10
                    : purchase.InsufficientBalance && catalogItem.PaysWithCash
                        ? (byte)50
                        : (byte)40;
                _log($"{channel}:{remote} 商城购买：mode={paymentMode} category={category} item={itemCode} quantity={quantity} currency={(catalogItem.PaysWithCash ? "Cash" : "Hans")} unitPrice={catalogItem.PurchasePrice} durationDays={catalogItem.DurationDays} result={resultCode} newQuantity={purchase.NewQuantity} hans={purchase.Hans} cash={purchase.Cash} error={purchase.Error}");
                return BuildNativeFrame(
                    frame,
                    0xC432,
                    BuildShopPurchaseResultPayload(
                        resultCode,
                        paymentMode,
                        itemCode,
                        purchase.NewQuantity,
                        purchase.Success ? (byte)1 : (byte)0,
                        purchase.Cash,
                        purchase.Hans),
                    session);
            }

            case 0xC3CD: // REQ_BUY_AVATAITEM -> C3CE
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != NanaPurchaseRequestPayloadLength)
                {
                    _log($"{channel}:{remote} NaNa purchase request length invalid: expected {NanaPurchaseRequestPayloadLength}, actual {payload.Length}; returning failure");
                    return BuildNativeFrame(
                        frame,
                        0xC3CE,
                        BuildNanaPurchaseResultPayload(40, 0, session.Character.Cash, session.Character.Hans),
                        session);
                }

                var paymentMode = payload[0];
                var itemCount = payload[NanaPurchaseItemCountOffset];
                var items = new List<(uint ItemCode, byte Option, uint UnitPrice)>(itemCount);
                var seenItemCodes = new HashSet<uint>();
                var valid = paymentMode == 4 && itemCount is > 0 and <= NanaPurchaseCapacity;
                if (valid)
                {
                    for (var index = 0; index < itemCount; index++)
                    {
                        var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(
                            payload.AsSpan(NanaPurchaseItemCodeOffset + index * sizeof(uint), sizeof(uint)));
                        var option = payload[NanaPurchaseItemOptionOffset + index];
                        if (!seenItemCodes.Add(itemCode)
                            || !ShopCatalog.TryGet(10, itemCode, out var catalogItem)
                            || catalogItem.Section != InventorySection.Clothing
                            || catalogItem.CashPrice == 0)
                        {
                            valid = false;
                            break;
                        }
                        items.Add((itemCode, option, catalogItem.CashPrice));
                    }
                }

                if (!valid)
                {
                    await RefreshSessionCharacterAsync(session, token);
                    _log($"{channel}:{remote} NaNa purchase rejected: mode={paymentMode} count={itemCount}; invalid client catalog selection");
                    return BuildNativeFrame(
                        frame,
                        0xC3CE,
                        BuildNanaPurchaseResultPayload(40, paymentMode, session.Character?.Cash ?? 0, session.Character?.Hans ?? 0),
                        session);
                }

                var purchase = await _database.PurchaseNanaAvatarItemsAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    paymentMode,
                    items.Select(item => (item.ItemCode, item.UnitPrice)).ToArray(),
                    token);
                await RefreshSessionCharacterAsync(session, token);
                var resultCode = purchase.Success ? (byte)10 : purchase.InsufficientBalance ? (byte)50 : (byte)40;
                _log($"{channel}:{remote} NaNa purchase: characterId={session.Character?.Id ?? 0} mode={paymentMode} items={string.Join(',', items.Select(item => $"{item.ItemCode}:{item.Option}"))} total={items.Sum(item => (long)item.UnitPrice)} result={resultCode} hans={purchase.Hans} cash={purchase.Cash} error={purchase.Error}");
                return BuildNativeFrame(
                    frame,
                    0xC3CE,
                    BuildNanaPurchaseResultPayload(
                        resultCode,
                        paymentMode,
                        session.Character?.Cash ?? purchase.Cash,
                        session.Character?.Hans ?? purchase.Hans),
                    session);
            }

            case 0xC40B: // REQ_BUY_INTERIORITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != InteriorPurchaseRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 装饰商店购买请求长度无效：期望 {InteriorPurchaseRequestPayloadLength}，实际 {payload.Length}；返回失败结果");
                    return BuildNativeFrame(
                        frame,
                        0xC40C,
                        BuildInteriorPurchaseResultPayload(40, 0, [], session.Character.Cash, session.Character.Hans),
                        session);
                }

                // The C40B constructor stores quantities in payload+8..+47
                // and the matching item codes in payload+48..+207. Bytes
                // +4..+6 are transient client flags and are not item data.
                var paymentModeValue = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var itemCount = payload[7];
                var requestedItems = new List<(uint ItemCode, ushort Quantity, uint UnitPrice)>(itemCount);
                var seenItemCodes = new HashSet<uint>();
                var requestIsValid = paymentModeValue == 4
                    && itemCount is > 0 and <= InteriorPurchaseRequestCapacity;
                if (requestIsValid)
                {
                    for (var index = 0; index < itemCount; index++)
                    {
                        var quantity = payload[8 + index];
                        var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(48 + index * sizeof(uint), sizeof(uint)));
                        if (quantity == 0
                            || !seenItemCodes.Add(itemCode)
                            || !ShopCatalog.TryGet(itemCode, out var catalogItem)
                            || catalogItem.Section != InventorySection.Furniture
                            || catalogItem.Category != 11
                            || catalogItem.HansPrice == 0)
                        {
                            requestIsValid = false;
                            break;
                        }
                        requestedItems.Add((itemCode, quantity, catalogItem.HansPrice));
                    }
                }

                if (!requestIsValid)
                {
                    await RefreshSessionCharacterAsync(session, token);
                    _log($"{channel}:{remote} 装饰商店购买请求无效：mode={paymentModeValue} count={itemCount}；返回协议失败结果");
                    return BuildNativeFrame(
                        frame,
                        0xC40C,
                        BuildInteriorPurchaseResultPayload(
                            40,
                            paymentModeValue <= byte.MaxValue ? (byte)paymentModeValue : (byte)0,
                            [],
                            session.Character?.Cash ?? 0,
                            session.Character?.Hans ?? 0),
                        session);
                }

                var purchase = await _database.PurchaseInteriorItemsAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    checked((byte)paymentModeValue),
                    requestedItems,
                    token);
                await RefreshSessionCharacterAsync(session, token);
                var resultCode = purchase.Success ? (byte)10 : purchase.InsufficientBalance ? (byte)50 : (byte)40;
                _log($"{channel}:{remote} 装饰商店购买：mode={paymentModeValue} count={itemCount} items={string.Join(',', requestedItems.Select(item => $"{item.ItemCode}x{item.Quantity}"))} totalPrice={requestedItems.Sum(item => (long)item.Quantity * item.UnitPrice)} result={resultCode} hans={purchase.Hans} cash={purchase.Cash} error={purchase.Error}");
                if (purchase.Success)
                    AccountStateChanged?.Invoke();
                return BuildNativeFrame(
                    frame,
                    0xC40C,
                    BuildInteriorPurchaseResultPayload(
                        resultCode,
                        checked((byte)paymentModeValue),
                        purchase.Success ? purchase.Items : [],
                        purchase.Cash,
                        purchase.Hans),
                    session);
            }

            case 0xC46F: // REQ_BUY_TOKENITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != SpecialTokenPurchaseRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 特殊道具购买请求长度无效：期望 {SpecialTokenPurchaseRequestPayloadLength}，实际 {payload.Length}；返回失败结果");
                    return BuildNativeFrame(frame, 0xC470, BuildShopPurchaseResultPayload(40, 0, 0, 0, 0, session.Character.Cash, session.Character.Hans), session);
                }

                // The C46F constructor is a fixed 16-byte frame:
                // ushort payment mode, ushort quantity, uint item code.
                var paymentMode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var quantity = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                var catalogItemFound = ShopCatalog.TryGet(itemCode, out var catalogItem)
                    && IsSupportedSpecialShopCategory(catalogItem.Category);
                if (paymentMode != 4 || quantity == 0 || !catalogItemFound || !catalogItem.IsPurchasable)
                {
                    await RefreshSessionCharacterAsync(session, token);
                    _log($"{channel}:{remote} 特殊商城购买请求不在客户端目录：mode={paymentMode} quantity={quantity} item={itemCode}；返回协议失败结果");
                    return BuildNativeFrame(frame, 0xC470, BuildShopPurchaseResultPayload(40, (byte)paymentMode, itemCode, 0, 0, session.Character?.Cash ?? 0, session.Character?.Hans ?? 0), session);
                }

                var purchase = await _database.PurchaseShopItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    (byte)paymentMode,
                    itemCode,
                    quantity,
                    catalogItem.PurchasePrice,
                    catalogItem.PaysWithCash,
                    token);
                await RefreshSessionCharacterAsync(session, token);
                var resultCode = purchase.Success
                    ? (byte)10
                    : purchase.InsufficientBalance && catalogItem.PaysWithCash
                        ? (byte)50
                        : (byte)40;
                _log($"{channel}:{remote} 特殊商城购买：mode={paymentMode} category={catalogItem.Category} item={itemCode} quantity={quantity} currency={(catalogItem.PaysWithCash ? "Cash" : "Hans")} unitPrice={catalogItem.PurchasePrice} result={resultCode} newQuantity={purchase.NewQuantity} hans={purchase.Hans} cash={purchase.Cash} error={purchase.Error}");
                return BuildNativeFrame(
                    frame,
                    0xC470,
                    BuildShopPurchaseResultPayload(
                        resultCode,
                        (byte)paymentMode,
                        itemCode,
                        purchase.NewQuantity,
                        purchase.Success ? (byte)1 : (byte)0,
                        purchase.Cash,
                        purchase.Hans),
                    session);
            }

            case 0xC491: // REQ_BUY_SPECIAL_DDAKGI -> C492
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != SpecialCardPurchaseRequestPayloadLength)
                {
                    _log($"{channel}:{remote} special-card purchase length invalid: expected={SpecialCardPurchaseRequestPayloadLength} actual={payload.Length}");
                    return BuildNativeFrame(frame, 0xC492, BuildSpecialCardPurchaseResultPayload(40, 0), session);
                }

                // Native C491: uint card code, ushort money type, ushort count.
                // Purchasable Sddakg._D35 rows use money type 4 (Cash).
                var cardCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var paymentType = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
                var quantity = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                if (paymentType != 4
                    || quantity == 0
                    || !CardCatalog.TryGet(cardCode, out var card)
                    || card.Category != 3
                    || !card.IsSpecialShopPurchasable
                    || card.SpecialShopPrice == 0)
                {
                    _log($"{channel}:{remote} special-card purchase rejected by Sddakg._D35: card={cardCode} moneyType={paymentType} quantity={quantity}");
                    return BuildNativeFrame(frame, 0xC492, BuildSpecialCardPurchaseResultPayload(40, paymentType), session);
                }

                var purchase = await _database.PurchaseSpecialCardAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    cardCode,
                    quantity,
                    token);
                await RefreshSessionCharacterAsync(session, token);
                ushort resultCode = purchase.Success
                    ? (ushort)0
                    : purchase.InsufficientBalance
                        ? (ushort)50
                        : (ushort)40;
                _log($"{channel}:{remote} special-card purchase: card={cardCode} quantity={quantity} unitCash={card.SpecialShopPrice} result={resultCode} newQuantity={purchase.NewQuantity} hans={purchase.Hans} cash={purchase.Cash} error={purchase.Error}");
                if (purchase.Success)
                    AccountStateChanged?.Invoke();
                return BuildNativeFrame(
                    frame,
                    0xC492,
                    BuildSpecialCardPurchaseResultPayload(resultCode, paymentType),
                    session);
            }

            case 0xC47A: // REQ_GIFT_ITEM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;

                await RefreshSessionCharacterAsync(session, token);
                if (payload.Length != ShopGiftRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 商城赠送请求长度无效：期望 {ShopGiftRequestPayloadLength}，实际 {payload.Length}；返回 FAIL_GIFT_ITEM");
                    return BuildNativeFrame(
                        frame,
                        0xC47B,
                        BuildShopGiftResultPayload(10, 0, session.Character?.Hans ?? 0, session.Character?.Cash ?? 0),
                        session);
                }

                // The native C47A constructor creates an 84-byte frame:
                // byte payment mode, byte combination flag, byte combination
                // subtype, byte quantity, uint item code, char recipient[16]
                // and char message[52]. Both character slots may retain junk
                // after their first NUL, so only the terminated recipient is read.
                var paymentMode = payload[0];
                var combinationFlag = payload[1];
                var combinationSubtype = payload[2];
                var quantity = payload[3];
                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                var cashFailureSubtype = paymentMode is >= 1 and <= 4 ? paymentMode : (byte)4;

                if (!TryDecodeGbkIdentity(
                        payload.AsSpan(ShopGiftRecipientOffset, ShopGiftRecipientLength),
                        out var recipientName,
                        out _))
                {
                    _log($"{channel}:{remote} 商城赠送收件角色名槽无效：item={itemCode}；返回 FAIL_GIFT_USER_NOT_FOUND");
                    return BuildNativeFrame(
                        frame,
                        0xC47B,
                        BuildShopGiftResultPayload(100, cashFailureSubtype, session.Character?.Hans ?? 0, session.Character?.Cash ?? 0),
                        session);
                }

                if (combinationFlag != 0)
                {
                    _log($"{channel}:{remote} 商城组合赠送不受当前客户端单物品目录支持：subtype={combinationSubtype} item={itemCode} recipient={recipientName}；返回 FAIL_COMBINE_BUY");
                    return BuildNativeFrame(
                        frame,
                        0xC47B,
                        BuildShopGiftResultPayload(80, cashFailureSubtype, session.Character?.Hans ?? 0, session.Character?.Cash ?? 0),
                        session);
                }

                if (!IsNativeShopPaymentMode(paymentMode))
                {
                    _log($"{channel}:{remote} 商城赠送付款模式无效：mode={paymentMode} item={itemCode} recipient={recipientName}；返回 FAIL_TOKEN_ERROR");
                    return BuildNativeFrame(
                        frame,
                        0xC47B,
                        BuildShopGiftResultPayload(70, cashFailureSubtype, session.Character?.Hans ?? 0, session.Character?.Cash ?? 0),
                        session);
                }

                if (quantity == 0)
                {
                    _log($"{channel}:{remote} 商城赠送数量无效：mode={paymentMode} item={itemCode} recipient={recipientName}；返回 FAIL_SHORTAGE_QUANTITY");
                    return BuildNativeFrame(
                        frame,
                        0xC47B,
                        BuildShopGiftResultPayload(20, cashFailureSubtype, session.Character?.Hans ?? 0, session.Character?.Cash ?? 0),
                        session);
                }

                if (CardCatalog.TryGet(itemCode, out var specialCard)
                    && specialCard.Category == 3
                    && specialCard.IsSpecialShopPurchasable
                    && specialCard.SpecialShopPrice > 0)
                {
                    if (paymentMode != 4)
                    {
                        _log($"{channel}:{remote} special-card gift rejected: moneyType={paymentMode} card={itemCode} recipient={recipientName}");
                        return BuildNativeFrame(
                            frame,
                            0xC47B,
                            BuildShopGiftResultPayload(70, cashFailureSubtype, session.Character?.Hans ?? 0, session.Character?.Cash ?? 0),
                            session);
                    }

                    var specialGift = await _database.GiftSpecialCardAsync(
                        session.AccountId,
                        session.Character.Id,
                        session.SessionId,
                        recipientName,
                        itemCode,
                        quantity,
                        token);
                    await RefreshSessionCharacterAsync(session, token);
                    ushort specialResultCode = specialGift.Success
                        ? (ushort)0
                        : !specialGift.RecipientFound
                            ? (ushort)100
                            : specialGift.QuantityLimit
                                ? (ushort)20
                                : specialGift.InsufficientBalance
                                    ? (ushort)50
                                    : (ushort)10;
                    var specialResponseHans = session.Character?.Hans ?? specialGift.Hans;
                    var specialResponseCash = session.Character?.Cash ?? specialGift.Cash;
                    _log($"{channel}:{remote} special-card gift: sender={session.Character?.Name} recipient={recipientName} card={itemCode} quantity={quantity} unitCash={specialCard.SpecialShopPrice} result={specialResultCode} recipientQuantity={specialGift.RecipientQuantity} hans={specialResponseHans} cash={specialResponseCash} error={specialGift.Error}");
                    if (specialGift.Success)
                        AccountStateChanged?.Invoke();
                    return BuildNativeFrame(
                        frame,
                        0xC47B,
                        BuildShopGiftResultPayload(specialResultCode, cashFailureSubtype, specialResponseHans, specialResponseCash),
                        session);
                }

                if (!ShopCatalog.TryGet(itemCode, out var catalogItem) || !catalogItem.IsPurchasable)
                {
                    _log($"{channel}:{remote} 商城赠送物品不在客户端可购买目录：mode={paymentMode} quantity={quantity} item={itemCode} recipient={recipientName}；返回 FAIL_GIFT_ITEM");
                    return BuildNativeFrame(
                        frame,
                        0xC47B,
                        BuildShopGiftResultPayload(10, cashFailureSubtype, session.Character?.Hans ?? 0, session.Character?.Cash ?? 0),
                        session);
                }

                var gift = await _database.GiftShopItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    recipientName,
                    itemCode,
                    quantity,
                    catalogItem.PurchasePrice,
                    catalogItem.PaysWithCash,
                    GetRequiredGiftRecipientGender(catalogItem),
                    token);
                await RefreshSessionCharacterAsync(session, token);

                ushort resultCode = gift.Success
                    ? (ushort)0
                    : !gift.RecipientFound
                        ? (ushort)100
                        : gift.GenderMismatch
                            ? (ushort)30
                            : gift.QuantityLimit
                                ? (ushort)20
                                : gift.InsufficientBalance
                                    ? (ushort)(catalogItem.PaysWithCash ? 50 : 40)
                                    : (ushort)10;
                var responseHans = session.Character?.Hans ?? gift.Hans;
                var responseCash = session.Character?.Cash ?? gift.Cash;
                _log($"{channel}:{remote} 商城赠送：sender={session.Character?.Name} recipient={recipientName} mode={paymentMode} quantity={quantity} category={catalogItem.Category} item={itemCode} currency={(catalogItem.PaysWithCash ? "Cash" : "Hans")} unitPrice={catalogItem.PurchasePrice} result={resultCode} recipientInboxQuantity={gift.RecipientInboxQuantity} hans={responseHans} cash={responseCash} error={gift.Error}");
                if (gift.Success)
                    AccountStateChanged?.Invoke();
                return BuildNativeFrame(
                    frame,
                    0xC47B,
                    BuildShopGiftResultPayload(resultCode, cashFailureSubtype, responseHans, responseCash),
                    session);
            }

            case 0xC365: // town entry: town id, town page and transient client context
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != TownEnterRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 城镇进入包长度无效：期望 {TownEnterRequestPayloadLength}，实际 {payload.Length}；未修改角色存档");
                    return null;
                }

                var townId = payload[0];
                var townPage = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var transientFlag = payload[4];
                var requestedPositionX = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                var requestedPositionY = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2));
                if (townPage > byte.MaxValue || transientFlag > 1)
                {
                    _log($"{channel}:{remote} 城镇进入上下文无效：town={townId} page={townPage} flag={transientFlag}；未修改角色存档");
                    return null;
                }

                var transition = TownPositionPolicy.ResolveTransition(townId, townPage, transientFlag);
                LeaveTradeRoomScene(session, "town enter");
                LeaveApartmentScene(session, "town enter");
                LeaveVillageShopScene(session, "town enter");
                LeaveTownScene(session, "town change");
                session.TownId = townId;
                session.TownPage = transition.Page;
                session.Character.CurrentMapId = townId;
                session.Character.CurrentTownPage = transition.Page;
                var townEntryRecovery = await ApplyNonCombatHealthRecoveryAsync(
                    session,
                    HealthRecoveryScene.Town,
                    token);
                if (townEntryRecovery.Changed)
                    _log($"{channel}:{remote} town entry recovery step: hp={townEntryRecovery.CurrentHp}/{session.Character!.MaxHp} mp={townEntryRecovery.CurrentMp}/{session.Character.MaxMp} restored=({townEntryRecovery.HpRestored},{townEntryRecovery.MpRestored})");
                // C365 runs while the old village actor/controller is being torn
                // down. Its X/Y describe transient transport context, not the new
                // actor's authoritative landing point. Preserve the last legal
                // position until the destination C367/CB21 chain reports one.
                _log($"{channel}:{remote} C365 village transition: selector={townId} requestedPage={townPage} mode={transientFlag} -> responsePage={transition.Page} responseFlag={transition.Flag} transientPosition=({requestedPositionX},{requestedPositionY}) retainedPosition=({session.LastReportedPositionX},{session.LastReportedPositionY}) canonicalized={transition.Canonicalized}");
                return BuildNativeFrame(
                    frame,
                    0xC366,
                    BuildTownEnterPayload(townId, transition.Page, transition.Flag),
                    session);
            }

            case 0xC367: // move town page: int32 page/sub-mode and transient uint16 X/Y
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != RoomEnterRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 房间进入包长度无效：期望 {RoomEnterRequestPayloadLength}，实际 {payload.Length}；未修改角色存档");
                    return null;
                }

                var roomIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
                var requestedPositionX = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
                var requestedPositionY = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                if (roomIndex is < 0 or > byte.MaxValue)
                {
                    _log($"{channel}:{remote} invalid room index={roomIndex}");
                    return null;
                }

                if (TryGetDungeonTransition(session, out var bridgeRoomId, out var bridgeAction))
                {
                    // Retail dungeon transitions unload the completed battle through
                    // the transient dungeon-entrance page. A room response sent beside
                    // C368 is consumed before that page finishes initializing, so
                    // wait for the page to finish before the client opens the
                    // game-service handshake and re-enters the retained room.
                    var transitionPosition = TownPositionPolicy.Normalize(
                        requestedPositionX,
                        requestedPositionY);
                    session.LastReportedPositionX = transitionPosition.X;
                    session.LastReportedPositionY = transitionPosition.Y;
                    session.TownPage = (byte)roomIndex;
                    session.TownSceneActive = false;
                    _log($"{channel}:{remote} Dungeon transition bridge entered: room={bridgeRoomId} action={bridgeAction} townPage={roomIndex} requested=({requestedPositionX},{requestedPositionY}) position=({transitionPosition.X},{transitionPosition.Y}); returned C368 and awaiting C36C before the client-driven game-service handshake");
                    return BuildNativeFrame(
                        frame,
                        0xC368,
                        BuildRoomEnterPayload(
                            session.Character,
                            (byte)roomIndex,
                            transitionPosition.X,
                            transitionPosition.Y),
                        session);
                }

                // A fresh client process always enters its one-shot FirstVillageFlag
                // actor path after C355. That path ignores C368 coordinates and reads
                // the loaded page's built-in first-entry point. Do not replace the
                // client's safe bootstrap C367 page with an arbitrary persisted page.
                var effectiveRoomIndex = roomIndex;
                var entryPosition = TownPositionPolicy.Normalize(
                    requestedPositionX,
                    requestedPositionY);
                var positionX = entryPosition.X;
                var positionY = entryPosition.Y;

                LeaveTradeRoomScene(session, "town page enter");
                LeaveApartmentScene(session, "town page enter");
                LeaveVillageShopScene(session, "town page enter");
                LeaveTownScene(session, "town page change");
                session.TownPage = (byte)effectiveRoomIndex;
                session.LastReportedPositionX = positionX;
                session.LastReportedPositionY = positionY;
                session.Character.CurrentMapId = session.TownId;
                session.Character.CurrentTownPage = effectiveRoomIndex;
                session.Character.PositionX = positionX;
                session.Character.PositionY = positionY;
                var townRecovery = await ApplyNonCombatHealthRecoveryAsync(
                    session,
                    HealthRecoveryScene.Town,
                    token);
                if (townRecovery.Changed)
                    _log($"{channel}:{remote} town recovery step: hp={townRecovery.CurrentHp}/{session.Character!.MaxHp} mp={townRecovery.CurrentMp}/{session.Character.MaxMp} restored=({townRecovery.HpRestored},{townRecovery.MpRestored})");
                _log(entryPosition.Repaired
                    ? $"{channel}:{remote} C367 village-entry sentinel normalized: town={session.TownId} room={effectiveRoomIndex} requested=({requestedPositionX},{requestedPositionY}) position=({positionX},{positionY}); FFFF/FFFF and legacy 03FF/03FF are not persisted"
                    : $"{channel}:{remote} C367 accepted village-entry position: room={effectiveRoomIndex} position=({positionX},{positionY})");
                return BuildNativeFrame(
                    frame,
                    0xC368,
                    BuildRoomEnterPayload(
                        session.Character,
                        (byte)effectiveRoomIndex,
                        positionX,
                        positionY),
                    session);
            }

            case 0xC376: // character/profile query
                if (!session.OnlineTracked) return null;
                if (payload.Length != ProfileQueryPayloadLength)
                {
                    _log($"{channel}:{remote} 角色资料查询长度无效：期望 {ProfileQueryPayloadLength}，实际 {payload.Length}；未响应且未修改角色存档");
                    return null;
                }

                var profileQueryMode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                if (profileQueryMode is not (100 or 200))
                {
                    _log($"{channel}:{remote} 角色资料查询模式无效：mode={profileQueryMode}；未响应且未修改角色存档");
                    return null;
                }

                var profileEntityId = BinaryPrimitives.ReadUInt16LittleEndian(
                    payload.AsSpan(ProfileQueryEntityIdOffset, sizeof(ushort)));
                if (!TryDecodeGbkIdentity(
                        payload.AsSpan(ProfileQueryIdentityOffset, ProfileQueryIdentityLength),
                        out var profileCharacterName,
                        out var profileCharacterNameLength))
                {
                    _log($"{channel}:{remote} 角色资料查询缺少有效的 NUL 结尾 GBK 角色名；未响应且未修改角色存档");
                    return null;
                }

                var profileTarget = _activeWorldSessions.Values
                    .Select(item => item.Session)
                    .FirstOrDefault(candidate =>
                        candidate.Character is { } character
                        && GetSceneEntityId(character) == profileEntityId
                        && string.Equals(character.Name, profileCharacterName, StringComparison.OrdinalIgnoreCase)
                        && (candidate.SessionId == session.SessionId
                            || IsSamePartyInvitationScene(session, candidate)));
                if (profileTarget is null)
                {
                    _log($"{channel}:{remote} 角色资料查询目标不在当前可见场景：entity={profileEntityId} nameLength={profileCharacterNameLength}；未响应且未修改角色存档");
                    return null;
                }

                // Read the persisted profile without replacing the target's live
                // runtime character (HP/MP/position may currently differ in a dungeon).
                var profileCharacter = await _database.GetCharacterByIdAsync(
                    profileTarget.Character!.Id,
                    token) ?? profileTarget.Character;
                _log($"{channel}:{remote} 角色资料查询成功：mode={profileQueryMode} entity={profileEntityId} nameLength={profileCharacterNameLength}");
                return BuildNativeFrame(frame, 0xC377, BuildProfileResponsePayload(profileCharacter), session);

            case 0xC4EA: // scene transition notification (client-to-adapter, no response)
                if (!session.OnlineTracked || payload.Length != 0)
                {
                    _log($"{channel}:{remote} 场景切换单向通知无效：online={session.OnlineTracked} payload={payload.Length}；无需响应且未修改场景状态");
                    return null;
                }

                // The original client has no C4EB/C4EC/C4ED response consumer.
                // Subsequent enter packets own the actual scene-state transition.
                _log($"{channel}:{remote} 宸插鐞嗗満鏅垏鎹㈠崟鍚戦€氱煡锛涘崗璁棤闇€鍝嶅簲锛岀瓑寰呭悗缁満鏅繘鍏ュ寘");
                return null;

            case 0xC5AA: // scene move request -> scene move answer
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未建立世界会话的场景跳转请求");
                    return null;
                }
                if (payload.Length != SceneTransitionRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 场景跳转请求长度无效：期望 {SceneTransitionRequestPayloadLength}，实际 {payload.Length}；未响应且未修改场景状态");
                    return null;
                }

                var sceneTransitionMode = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                var sceneTransitionAction = sceneTransitionMode switch
                {
                    1 => (ushort)20,
                    10 => (ushort)11,
                    _ => (ushort)0
                };
                var sceneTransitionPayload = new byte[SceneTransitionResponsePayloadLength];
                BinaryPrimitives.WriteUInt16LittleEndian(sceneTransitionPayload, sceneTransitionAction);
                _log($"{channel}:{remote} 场景跳转请求已处理：mode={sceneTransitionMode} action={sceneTransitionAction}；场景状态交由后续进入包更新");
                return BuildNativeFrame(frame, 0xC5AB, sceneTransitionPayload, session);

            case 0xC5B0: // REQ_AUCTION_LIST -> ANS_AUCTION_LIST
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;

                if (payload.Length != AuctionListRequestPayloadLength)
                {
                    _log($"{channel}:{remote} auction list request length invalid: {payload.Length}");
                    return null;
                }

                var requestType = payload[0];
                if (requestType == 2)
                {
                    _log($"{channel}:{remote} auction extended personal list rejected by retail result 13");
                    await Task.Delay(AuctionUiResponseDelay, token);
                    return BuildNativeFrame(frame, 0xC5B1, BuildAuctionListPayload(13, 0, [], session.Character.Id), session);
                }
                if (requestType is not (0 or 1 or 3 or 4))
                {
                    _log($"{channel}:{remote} auction list request type invalid: {requestType}");
                    await Task.Delay(AuctionUiResponseDelay, token);
                    return BuildNativeFrame(frame, 0xC5B1, BuildAuctionListPayload(12, 0, [], session.Character.Id), session);
                }

                var personal = requestType == 1;
                byte ddakgiType = 0;
                byte sortType = 0;
                byte pageSize = personal ? (byte)3 : (byte)AuctionListEntryCapacity;
                ushort page = 1;
                ushort ddakgiNumber = 0;
                string? sellerCharacterName = null;
                if (requestType is 3 or 4)
                {
                    ddakgiType = payload[1];
                    sortType = payload[2];
                    pageSize = payload[3];
                    page = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
                    ddakgiNumber = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                    if (ddakgiType is not (0 or 12 or 13 or 22 or 50)
                        || sortType is not (0 or 10 or 11 or 20 or 21)
                        || pageSize is 0 or > AuctionListEntryCapacity
                        || page == 0)
                    {
                        _log($"{channel}:{remote} auction search fields invalid: type={ddakgiType} sort={sortType} pageSize={pageSize} page={page}");
                        await Task.Delay(AuctionUiResponseDelay, token);
                        return BuildNativeFrame(frame, 0xC5B1, BuildAuctionListPayload(14, 0, [], session.Character.Id), session);
                    }
                    if (requestType == 4
                        && !TryDecodeGbkIdentity(payload.AsSpan(8, 16), out sellerCharacterName, out _))
                    {
                        _log($"{channel}:{remote} auction seller-name filter is not valid NUL-terminated GBK");
                        await Task.Delay(AuctionUiResponseDelay, token);
                        return BuildNativeFrame(frame, 0xC5B1, BuildAuctionListPayload(14, 0, [], session.Character.Id), session);
                    }
                }

                var query = await _database.QueryAuctionListingsAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    personal,
                    ddakgiType,
                    sortType,
                    page,
                    pageSize,
                    ddakgiNumber,
                    sellerCharacterName,
                    token);
                _log($"{channel}:{remote} auction list: requestType={requestType} result={query.ResultCode} page={page}/{query.TotalPages} entries={query.Listings.Count}");
                // The retail C5B0 senders create their wait overlay after
                // sending. sub_61CE30 handles C5B1 and releases UI type 13
                // (mapped by sub_7EA900 to popup kind 9), but only after the
                // popup base active flag is set. Keep loopback replies behind
                // several UI updates so that modal input shield is removable.
                await Task.Delay(AuctionUiResponseDelay, token);
                return BuildNativeFrame(
                    frame,
                    0xC5B1,
                    BuildAuctionListPayload(query.ResultCode, query.TotalPages, query.Listings, session.Character.Id),
                    session);
            }

            case 0xC5B2: // REQ_BUY_AUCTION_ITEM -> ANS_BUY_AUCTION_ITEM
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != AuctionBuyRequestPayloadLength)
                    return null;
                var uniqueNumber = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0, 8));
                var totalHans = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
                var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12, 4));
                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(16, 4));
                var result = await _database.PurchaseAuctionListingAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    uniqueNumber,
                    totalHans,
                    itemCount,
                    itemCode,
                    token);
                if (result.ResultCode == 1)
                    session.Character.Hans = result.Hans;
                _log($"{channel}:{remote} auction purchase: unique={uniqueNumber} item={itemCode} count={itemCount} total={totalHans} result={result.ResultCode}");
                // sub_630160 sends C5B2 before registering its wait overlay.
                // sub_61C620 consumes C5B3, changes the purchase state and
                // immediately sends C5B0; the following delayed C5B1 is the
                // retail path that finally removes the active wait overlay.
                await Task.Delay(AuctionUiResponseDelay, token);
                return BuildNativeFrame(frame, 0xC5B3, BuildAuctionResultPayload(result.ResultCode), session);
            }

            case 0xC5B4: // REQ_AUCTION_REG_ITEM -> ANS_AUCTION_REG_ITEM
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != AuctionRegisterRequestPayloadLength)
                    return null;
                var requestType = payload[0];
                var itemCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                var hansPerItem = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
                var result = await _database.RegisterAuctionListingAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    requestType,
                    itemCode,
                    itemCount,
                    hansPerItem,
                    token);
                _log($"{channel}:{remote} auction register: requestType={requestType} item={itemCode} count={itemCount} unit={hansPerItem} unique={result.UniqueNumber} result={result.ResultCode}");
                return BuildNativeFrame(
                    frame,
                    0xC5B5,
                    BuildAuctionRegistrationResultPayload(result.ResultCode, result.UniqueNumber),
                    session);
            }

            case 0xC5B6: // REQ_RETRIEVAL_AUCTION_ITEM -> ANS_RETRIEVAL_AUCTION_ITEM
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != AuctionRetrievalRequestPayloadLength)
                    return null;
                var requestType = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var uniqueNumber = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(8, 8));
                var result = await _database.RetrieveAuctionListingAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    requestType,
                    uniqueNumber,
                    token);
                if (result.ResultCode == 1)
                    session.Character.Hans = result.Hans;
                _log($"{channel}:{remote} auction retrieval: requestType={requestType} unique={uniqueNumber} returned={result.ReturnedQuantity} result={result.ResultCode}");
                return BuildNativeFrame(frame, 0xC5B7, BuildAuctionResultPayload(result.ResultCode), session);
            }

            case 0xC353: // SKIP_MAIN_GUIDE / CLEAR_MAIN_GUIDE followed by C594
            {
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 忽略未建立世界会话的教程完成事件");
                    return null;
                }

                if (payload.Length != MainGuideEventPayloadLength)
                {
                    _log($"{channel}:{remote} 主线引导完成事件长度无效：期望 {MainGuideEventPayloadLength}，实际 {payload.Length}；未修改角色存档");
                    return null;
                }

                if (!TryDecodeGbkIdentity(
                        payload.AsSpan(0, MainGuideIdentityLength),
                        out var identity,
                        out var identityLength))
                {
                    _log($"{channel}:{remote} 主线引导完成事件缺少有效的 NUL 结尾 GBK 身份；未修改角色存档");
                    return null;
                }

                if (!SessionIdentityMatches(identity, session))
                {
                    _log($"{channel}:{remote} 主线引导完成事件身份与活动会话不匹配：identityLength={identityLength} accountId={session.AccountId}；未修改角色存档");
                    return null;
                }

                var petVariant = NormalizeTutorialPetVariant(
                    payload.AsSpan(MainGuideIdentityLength, sizeof(uint)));

                if (session.Character.TutorialCompleted)
                {
                    _log($"{channel}:{remote} 忽略已完成角色的重复主线引导事件；保留当前位置=({session.Character.PositionX},{session.Character.PositionY}) 与宠物变体={session.Character.PetVariant}");
                    return BuildLoadNecessityReadinessResponse(frame, session);
                }

                var persisted = await _database.MarkTutorialCompletedAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    petVariant,
                    token);
                await RefreshSessionCharacterAsync(session, token);
                if (persisted)
                {
                    _log($"{channel}:{remote} 已原子保存主线引导完成、出生点与宠物变体：petVariant={session.Character?.PetVariant ?? petVariant}");
                    AccountStateChanged?.Invoke();
                    return BuildLoadNecessityReadinessResponse(
                        frame,
                        session);
                }

                _log($"{channel}:{remote} 主线引导完成状态未保存：账号、角色或活动世界会话已失效");
                return null;
            }

            case 0xC369: // request another town entity; C36A is the fixed entity response
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != TownUserInfoRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 城镇用户查询长度无效：期望 {TownUserInfoRequestPayloadLength}，实际 {payload.Length}；未修改角色存档");
                    return null;
                }

                var requestedEntityId = BinaryPrimitives.ReadInt32LittleEndian(payload);
                var peer = _activeWorldSessions.Values
                    .Where(item => IsSameTownPage(session, item.Session))
                    .Where(item => item.Session.SessionId != session.SessionId)
                    .Where(item => requestedEntityId <= 0 || item.CharacterId == requestedEntityId)
                    .OrderBy(item => item.CharacterId)
                    .FirstOrDefault();
                if (peer is null)
                {
                    _log($"{channel}:{remote} 城镇用户查询 value={requestedEntityId}：没有其他匹配的在线实体；不响应且不修改存档");
                    return null;
                }

                var peerCharacter = await _database.GetCharacterByIdAsync(peer.CharacterId, token);
                if (peerCharacter is null)
                    return null;
                _log($"{channel}:{remote} 城镇用户查询 value={requestedEntityId}：返回 entity={peer.CharacterId} name={peerCharacter.Name}");
                return BuildNativeFrame(
                    frame,
                    0xC36A,
                    BuildTownUserInfoPayload(
                        peerCharacter,
                        peer.Session.LastReportedPositionX,
                        peer.Session.LastReportedPositionY),
                    session);
            }

            case 0xC36C: // one-way town-page completion event, no payload
                if (payload.Length == 0 && session.OnlineTracked && session.Character is not null)
                {
                    if (TryGetDungeonTransition(session, out var reentryRoomId, out var reentryAction))
                    {
                        // C36C is one-way. The retail client waits for the normal
                        // town-page entity initialization before it opens CF09;
                        // omitting those broadcasts makes it time out and close the
                        // world connection. Keep the retained dungeon room and do
                        // not persist this bridge page's temporary coordinates.
                        var initializedTransitionTownScene = !session.TownSceneActive;
                        if (initializedTransitionTownScene)
                        {
                            session.TownSceneActive = true;
                            QueueTownEntitySnapshots(session);
                            if (GetEquippedPetItemCode(session.Character) != 0
                                && _activeWorldSessions.TryGetValue(session.SessionId, out var transitionSelfTarget))
                            {
                                session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                                    transitionSelfTarget,
                                    0xC47F,
                                    BuildUserDataChangePayload(session.Character),
                                    "restore equipped pet on dungeon transition bridge"));
                            }
                        }

                        // The previously verified Super-BOSS client path can rebuild
                        // the temporary town page without ever opening CF09. Restore
                        // the retained owner-room response used by that path so it can
                        // continue with C587/CF70/CFEB on this connection. Other result
                        // actions keep the client-driven CF09 -> CF6C/CF77 sequence.
                        var reentryAlreadySent = false;
                        if (reentryAction == DungeonSettlementAction.ChallengeBoss
                            && TryBuildDungeonTransitionReentry(
                                session,
                                DungeonTransitionReentryTrigger.TownPage,
                                out var reentryOpcode,
                                out var reentryPayload,
                                out var reentryRole,
                                out reentryAlreadySent)
                            && _activeWorldSessions.TryGetValue(
                                session.SessionId,
                                out var reentryTarget))
                        {
                            session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                                reentryTarget,
                                reentryOpcode,
                                reentryPayload,
                                $"dungeon transition {reentryAction} room reentry after C36C"));
                            _log($"{channel}:{remote} Dungeon transition town-page bridge restored previous Super-BOSS reentry: room={reentryRoomId} action={reentryAction} role={reentryRole} reentry=0x{reentryOpcode:X4}");
                        }
                        else if (reentryAction == DungeonSettlementAction.ChallengeBoss)
                        {
                            _log(reentryAlreadySent
                                ? $"{channel}:{remote} Dungeon transition duplicate Super-BOSS C36C reentry ignored: room={reentryRoomId} action={reentryAction}"
                                : $"{channel}:{remote} Dungeon transition Super-BOSS retained-room reentry unavailable after C36C: room={reentryRoomId} action={reentryAction}");
                        }

                        await RestoreDungeonVitalsAsync(session, false, token);
                        if (initializedTransitionTownScene)
                            QueueUserAutoHealing(session, session, "restore HP/MP on dungeon transition bridge");
                        _log(reentryAction == DungeonSettlementAction.ChallengeBoss
                            ? $"{channel}:{remote} Dungeon transition town-page bridge initialized: room={reentryRoomId} action={reentryAction}; previous retained-room reentry restored"
                            : $"{channel}:{remote} Dungeon transition town-page bridge initialized: room={reentryRoomId} action={reentryAction}; waiting for the retail CF09 then CF6C/CF77 request");
                        return null;
                    }

                    var initializedTownScene = !session.TownSceneActive;
                    await RestoreDungeonVitalsAsync(session, false, token);
                    var saved = await _database.SaveCharacterRuntimeStateAsync(
                        session.AccountId,
                        session.Character.Id,
                        session.SessionId,
                        CreateRuntimeState(session.Character, session.ChannelId),
                        token);
                    _log($"{channel}:{remote} 城镇页面完成通知；入口坐标存档结果={(saved ? "success" : "failed")}，无需响应");
                    if (initializedTownScene)
                    {
                        session.TownSceneActive = true;
                        QueueTownEntitySnapshots(session);
                        if (GetEquippedPetItemCode(session.Character) != 0
                            && _activeWorldSessions.TryGetValue(session.SessionId, out var townSelfTarget))
                        {
                            session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                                townSelfTarget,
                                0xC47F,
                                BuildUserDataChangePayload(session.Character),
                                "restore equipped pet after town page completion"));
                            _log($"{channel}:{remote} 城镇页面完成后补发宠物外观：item={GetEquippedPetItemCode(session.Character)}");
                        }
                        QueueUserAutoHealing(session, session, "restore town HP/MP after C36C");
                    }
                }
                else
                {
                    _log($"{channel}:{remote} 城镇页面完成通知无效：online={session.OnlineTracked} payload={payload.Length}；未修改角色存档");
                }
                return null;

            case 0xCF09: // REQ_GAME_CONNECTION -> ANS_GAME_CONNECTION
                if (payload.Length != 56)
                {
                    _log($"{channel}:{remote} 游戏连接请求长度无效：期望 56，实际 {payload.Length}；未响应且未修改存档");
                    return null;
                }
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (!TryDecodeGbkIdentity(payload.AsSpan(0, 18), out var arenaIdentity, out _)
                        || !TryRestoreAuxiliaryGameSession(arenaIdentity, remoteIp, session))
                    {
                        _log($"{channel}:{remote} 天空竞技场游戏连接身份恢复失败；identity={(string.IsNullOrWhiteSpace(arenaIdentity) ? "invalid" : arenaIdentity)}，未响应");
                        return null;
                    }
                    session.ArenaGameType = GetArenaGameTypeFromPort(session.ListenerPort);
                    _activeArenaSessions[session.SessionId] = session;
                    _log($"{channel}:{remote} 澶╃┖绔炴妧鍦烘父鎴忚繛鎺ユ彙鎵嬫垚鍔燂細type={session.ArenaGameType} character={session.Character!.Name} account={session.Username}");
                    return BuildNativeFrame(frame, 0xCF0A, BuildGameConnectionPayload(), session);
                }
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未建立世界会话的地宫游戏连接请求");
                    return null;
                }
                LeaveTradeRoomScene(session, "dungeon connection");
                LeaveApartmentScene(session, "dungeon connection");
                LeaveVillageShopScene(session, "dungeon connection");
                LeaveTownScene(session, "dungeon connection");
                _log($"{channel}:{remote} 地宫游戏连接握手成功：room={session.TownPage} character={session.Character.Name}");
                if (TryGetDungeonTransition(session, out var connectionReentryRoomId, out var connectionReentryAction))
                {
                    MarkDungeonTransitionGameConnected(session);
                    _log($"{channel}:{remote} Dungeon transition game connection restored: room={connectionReentryRoomId} action={connectionReentryAction}; waiting for the retail CF6C/CF77 request after CF0A");
                }
                return BuildNativeFrame(frame, 0xCF0A, BuildGameConnectionPayload(), session);

            case 0xCF0B: // REQ_ENTER_GAME_LOBBY -> ANS_ENTER_GAME_LOBBY
                if (!IsEntertainmentSession(channel, session)
                    || !session.OnlineTracked
                    || !session.AuxiliaryGameSession
                    || session.Character is null
                    || payload.Length != 0)
                    return null;
                return BuildNativeFrame(
                    frame,
                    0xCF0C,
                    EntertainmentProtocol.BuildLobbyInfo(
                        checked((uint)session.Character.Id),
                        session.ArenaGameType,
                        0,
                        0),
                    session);

            case 0xCF13: // REQ_GAME_RANKING -> ANS_GAME_RANKING
                if (!IsEntertainmentSession(channel, session)
                    || !session.OnlineTracked
                    || session.Character is null
                    || payload.Length is not (0 or 4))
                    return null;
                return BuildNativeFrame(frame, 0xCF14, EntertainmentProtocol.BuildRanking(), session);

            case 0xCF19: // REQ_GAME_WAIT_USERS -> ANS_GAME_WAIT_USERS
                if (!IsEntertainmentSession(channel, session)
                    || !session.OnlineTracked
                    || session.Character is null
                    || payload.Length is not (0 or 4))
                    return null;
                return BuildNativeFrame(
                    frame,
                    0xCF1A,
                    BuildEntertainmentWaitUsersPayload(session),
                    session);

            case 0xCF0D: // REQ_FLYSHOOTING_LOBBY_INFO -> ANS_FLYSHOOTING_LOBBY_INFO
                if (!string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal)
                    || !session.OnlineTracked
                    || !session.AuxiliaryGameSession
                    || session.Character is null
                    || payload.Length != 0)
                {
                    _log($"{channel}:{remote} Rejected invalid arena lobby-info request: auxiliary={session.AuxiliaryGameSession} online={session.OnlineTracked} payload={payload.Length}");
                    return null;
                }
                return BuildNativeFrame(
                    frame,
                    0xCF0E,
                    BuildArenaLobbyInfoPayload(session.Character, session.ArenaGameType),
                    session);

            case 0xCF11: // REQ_FLYSHOOTING_GAMEROOM_LIST -> ANS_FLYSHOOTING_GAMEROOM_LIST
                if (!string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal)
                    || !session.OnlineTracked
                    || !session.AuxiliaryGameSession
                    || session.Character is null
                    || payload.Length != 8)
                {
                    _log($"{channel}:{remote} Rejected invalid arena room-list request: auxiliary={session.AuxiliaryGameSession} online={session.OnlineTracked} payload={payload.Length}");
                    return null;
                }
                return BuildNativeFrame(frame, 0xCF12, BuildArenaRoomListPayload(session), session);

            case 0xCF17: // REQ_ARENA_GAMERANKING_INFO -> ANS_ARENA_GAMERANKING_INFO
            {
                if (!string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal)
                    || !session.OnlineTracked
                    || !session.AuxiliaryGameSession
                    || session.Character is null
                    || payload.Length != 4)
                {
                    _log($"{channel}:{remote} Rejected invalid arena ranking request: auxiliary={session.AuxiliaryGameSession} online={session.OnlineTracked} payload={payload.Length}");
                    return null;
                }
                var rankingType = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                if (rankingType is not (10u or 20u))
                {
                    _log($"{channel}:{remote} Rejected unknown arena ranking type: type={rankingType}");
                    return null;
                }
                return BuildNativeFrame(frame, 0xCF18, BuildEmptyArenaRankingPayload(rankingType), session);
            }

            case 0xCF0F: // REQ_GAMELIST -> ANS_GAMELIST
                if (!session.OnlineTracked || session.Character is null || payload.Length != 4)
                    return null;
                if (IsEntertainmentSession(channel, session))
                {
                    if (!EntertainmentProtocol.TryParseRoomListRequest(
                            payload,
                            out var entertainmentStartRoomId,
                            out var entertainmentRequestType,
                            out var entertainmentOption))
                        return null;
                    return BuildNativeFrame(
                        frame,
                        0xCF10,
                        BuildEntertainmentRoomListPayload(
                            session,
                            entertainmentStartRoomId,
                            entertainmentRequestType,
                            entertainmentOption),
                        session);
                }
                LeaveTradeRoomScene(session, "dungeon room list");
                LeaveApartmentScene(session, "dungeon room list");
                LeaveVillageShopScene(session, "dungeon room list");
                LeaveTownScene(session, "dungeon room list");
                return BuildNativeFrame(
                    frame,
                    0xCF10,
                    BuildDungeonRoomListPayload(session),
                    session);

            case 0xCF6C: // REQ_CREATE_GAME -> ANS_CREATE_GAME
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (!session.OnlineTracked
                            || !session.AuxiliaryGameSession
                            || session.Character is null
                            || !EntertainmentProtocol.TryParseCreateRequest(
                                payload,
                                out var entertainmentCreateRequest)
                            || entertainmentCreateRequest is null)
                            return null;
                        var entertainmentRoom = CreateEntertainmentRoom(
                            session,
                            entertainmentCreateRequest);
                        QueueEntertainmentLobbyRoomListRefresh(session, "entertainment room created");
                        _log($"{channel}:{remote} Entertainment room created: room={entertainmentRoom.Id} type={entertainmentRoom.GameType} owner={session.Character.Name} mode={entertainmentRoom.CreateRequest.Mode}");
                        return BuildNativeFrame(
                            frame,
                            0xCF6D,
                            BuildEntertainmentCreateResponse(entertainmentRoom, session.Character),
                            session);
                    }
                    if (!session.OnlineTracked
                        || !session.AuxiliaryGameSession
                        || session.Character is null
                        || session.ArenaGameType is < 1 or > 4
                        || !ArenaProtocol.TryParseCreateRequest(payload, out var arenaCreateRequest)
                        || arenaCreateRequest is null)
                    {
                        _log($"{channel}:{remote} Rejected invalid arena create-room request: auxiliary={session.AuxiliaryGameSession} online={session.OnlineTracked} payload={payload.Length}");
                        return null;
                    }

                    var arenaRoom = CreateArenaRoom(session, arenaCreateRequest);
                    QueueArenaLobbyRoomListRefresh(session, "arena room created");
                    _log($"{channel}:{remote} Arena room created from retail CF6C: room={arenaRoom.Id} type={arenaRoom.GameType} metadata={arenaRoom.CreateRequest.Metadata} responseFields={arenaRoom.ResponseField2}/{arenaRoom.ResponseField3} owner={session.Character.Name} title={arenaRoom.Title} password={(!string.IsNullOrEmpty(arenaRoom.Password))}");
                    return BuildNativeFrame(
                        frame,
                        0xCF6D,
                        BuildArenaCreateGamePayload(arenaRoom, session.Character),
                        session);
                }
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未建立世界会话的地宫建房请求");
                    return null;
                }
                if (payload.Length != 44)
                {
                    _log($"{channel}:{remote} 地宫建房请求长度无效：期望 44，实际 {payload.Length}；未响应且未修改存档");
                    return null;
                }
                var requestedDifficulty = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(30, 2));
                DungeonRoom? transitionCreateRoom = null;
                string? transitionCreateRejection = null;
                lock (_dungeonRoomGate)
                {
                    if (_dungeonRooms.TryGetValue(session.DungeonRoomId, out var retainedRoom)
                        && retainedRoom.HasPendingTransition
                        && retainedRoom.TransitioningSessionIds.Contains(session.SessionId))
                    {
                        if (!retainedRoom.Members.ContainsKey(session.SessionId)
                            || retainedRoom.OwnerSessionId != session.SessionId)
                        {
                            transitionCreateRejection = "requester is no longer the retained room owner";
                        }
                        else if (!retainedRoom.TransitionDisconnectedSessionIds.Contains(session.SessionId)
                                 || !retainedRoom.TransitionGameConnectedSessionIds.Contains(session.SessionId))
                        {
                            transitionCreateRejection = "retail CF1D and CF09/CF0A transition handshakes are incomplete";
                        }
                        else if (payload[26] != retainedRoom.HdIndex
                                 || payload[27] != retainedRoom.PendingEpisode
                                 || payload[28] != retainedRoom.PendingDungeon
                                 || !IsRetainedDungeonStageSelectorAccepted(retainedRoom, payload[29])
                                 || !IsRetainedDungeonDifficultySelectorAccepted(
                                     retainedRoom,
                                     requestedDifficulty))
                        {
                            var normalDifficulty = EncodeDungeonDifficultySelector(
                                retainedRoom.PendingLogicalDifficulty,
                                superBoss: false);
                            transitionCreateRejection =
                                $"selectors={payload[26]}/{payload[27]}/{payload[28]}/{payload[29]}/{requestedDifficulty} expectedTarget={retainedRoom.HdIndex}/{retainedRoom.PendingEpisode}/{retainedRoom.PendingDungeon}/{retainedRoom.PendingStage}/{retainedRoom.Difficulty} sourceNormalDifficulty={normalDifficulty}";
                        }
                        else
                        {
                            // The retail owner re-enters through CF6C after CF8C.
                            // This must retain the waiting room; CF7F alone commits Pending*.
                            retainedRoom.TransitionReenteredSessionIds.Add(session.SessionId);
                            transitionCreateRoom = retainedRoom;
                        }
                    }
                }
                if (transitionCreateRejection is not null)
                {
                    _log($"{channel}:{remote} Rejected dungeon transition CF6C reentry: room={session.DungeonRoomId} character={session.Character.Id} reason={transitionCreateRejection}");
                    return null;
                }
                if (transitionCreateRoom is not null)
                {
                    _log($"{channel}:{remote} Dungeon transition CF6C reused retained room: room={transitionCreateRoom.Id} action={transitionCreateRoom.SettlementAction} owner={session.Character.Id} target={transitionCreateRoom.PendingEpisode}/{transitionCreateRoom.PendingDungeon}/{transitionCreateRoom.PendingStage} difficulty={transitionCreateRoom.Difficulty} members={transitionCreateRoom.Members.Count}");
                    return BuildCreateGameResponse(frame, session);
                }
                if (requestedDifficulty >= DungeonDifficultyCount
                    || !DungeonCombatCatalog.HasStage(
                        payload[26], payload[27], payload[28], payload[29]))
                {
                    _log($"{channel}:{remote} Rejected unavailable dungeon creation: character={session.Character.Id} hd={payload[26]} episode={payload[27]} dungeon={payload[28]} realStage={payload[29]} difficulty={requestedDifficulty}");
                    return null;
                }
                var requestedLogicalDifficulty = DecodeDungeonLogicalDifficulty(
                    payload[28],
                    payload[29],
                    requestedDifficulty);
                if (!await CanCharacterEnterDungeonSelectionAsync(
                        session.Character,
                        payload[27],
                        payload[28],
                        requestedLogicalDifficulty,
                        token))
                {
                    _log($"{channel}:{remote} Rejected locked dungeon creation: character={session.Character.Id} episode={payload[27]} dungeon={payload[28]} realStage={payload[29]} wireDifficulty={requestedDifficulty} logicalDifficulty={requestedLogicalDifficulty} level={session.Character.Level}");
                    return null;
                }
                var createdRoom = CreateDungeonRoom(session, payload);
                await RestoreDungeonVitalsAsync(session, false, token);
                _log($"{channel}:{remote} 创建地宫房间成功：room={createdRoom.Id} owner={session.Character.Id} members=1");
                return BuildCreateGameResponse(frame, session);

            case 0xCF75: // REQ_ENTER_GAMEROOM -> ANS_ENTER_GAMEROOM
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != 12)
                    return null;
                var requestedRoomId = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (!session.AuxiliaryGameSession
                            || !TryDecodeFixedGbkString(payload.AsSpan(4, 8), true, out var entertainmentPassword)
                            || !TryJoinEntertainmentRoom(
                                session,
                                requestedRoomId,
                                entertainmentPassword,
                                out var joinedEntertainmentRoom)
                            || joinedEntertainmentRoom is null
                            || !joinedEntertainmentRoom.Members.TryGetValue(
                                joinedEntertainmentRoom.OwnerSessionId,
                                out var entertainmentOwner)
                            || entertainmentOwner.Character is null)
                            return BuildNativeFrame(
                                frame,
                                0xCF76,
                                EntertainmentProtocol.BuildEnterResponse(20, 0, 0, string.Empty, string.Empty, 0, string.Empty),
                                session);
                        QueueEntertainmentLobbyRoomListRefresh(session, "entertainment room member joined");
                        return BuildNativeFrame(
                            frame,
                            0xCF76,
                            BuildEntertainmentEnterResponse(joinedEntertainmentRoom, entertainmentOwner.Character),
                            session);
                    }
                    if (!session.AuxiliaryGameSession
                        || !TryDecodeFixedGbkString(payload.AsSpan(4, 8), true, out var requestedPassword)
                        || !TryJoinArenaRoom(session, requestedRoomId, requestedPassword, out var joinedArenaRoom)
                        || joinedArenaRoom is null
                        || !joinedArenaRoom.Members.TryGetValue(joinedArenaRoom.OwnerSessionId, out var joinedArenaOwner)
                        || joinedArenaOwner.Character is null)
                    {
                        _log($"{channel}:{remote} Arena listed-room entry rejected: room={requestedRoomId} character={session.Character.Name}");
                        return BuildNativeFrame(frame, 0xCF76, BuildArenaLobbyEnterFailurePayload(), session);
                    }
                    _log($"{channel}:{remote} Arena listed-room entry accepted: room={joinedArenaRoom.Id} type={joinedArenaRoom.GameType} character={session.Character.Name} slot={session.ArenaSlotIndex}");
                    QueueArenaLobbyRoomListRefresh(session, "arena room member joined");
                    return BuildNativeFrame(
                        frame,
                        0xCF76,
                        BuildArenaLobbyEnterPayload(joinedArenaRoom, joinedArenaOwner.Character),
                        session);
                }
                var requestedDungeonRoom = GetDungeonRoomById(requestedRoomId);
                var selectionAllowed = requestedDungeonRoom is not null
                    && await CanCharacterEnterDungeonSelectionAsync(
                        session.Character,
                        requestedDungeonRoom.Episode,
                        requestedDungeonRoom.Dungeon,
                        requestedDungeonRoom.BattleLogicalDifficulty,
                        token);
                DungeonRoom? joinedRoom = null;
                var joined = selectionAllowed && TryJoinDungeonRoom(
                    session, requestedRoomId, out joinedRoom, payload.AsSpan(4, 8).ToArray());
                if (joined)
                    await RestoreDungeonVitalsAsync(session, false, token);
                var visibleRoom = joinedRoom ?? requestedDungeonRoom;
                var visibleOwner = visibleRoom is null
                    ? null
                    : visibleRoom.Members.GetValueOrDefault(visibleRoom.OwnerSessionId)?.Session.Character;
                if (visibleRoom is null || visibleOwner is null)
                    return null;
                var enterRoomPayload = DungeonProtocol.BuildEnterResponse(
                    checked((ushort)visibleRoom.Id),
                    visibleOwner.Id,
                    visibleOwner.Name,
                    DecodeDungeonTitle(visibleRoom),
                    DecodeDungeonPassword(visibleRoom),
                    DecodeDungeonDifficulty(visibleRoom),
                    joined ? (byte)10 : (byte)20);
                _log($"{channel}:{remote} 大厅进入地宫房间：requested={requestedRoomId} joined={joined} room={joinedRoom?.Id ?? 0} character={session.Character.Id}");
                return BuildNativeFrame(frame, 0xCF76, enterRoomPayload, session);
            }

            case 0xCF77: // REQ_FLYSHOOTING_ENTER_GAMEROOM -> ANS_FLYSHOOTING_ENTER_GAMEROOM
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != 8)
                    return null;
                var quickMode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                if (quickMode is not (10 or 20 or 100))
                    return null;
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                        return null;
                    if (!session.AuxiliaryGameSession
                        || session.ArenaGameType is < 1 or > 4
                        || !ArenaProtocol.TryParseQuickEnterRequest(payload, out var arenaEnterRequest))
                        return null;
                    var arenaRoom = QuickEnterArenaRoom(session, arenaEnterRequest, out var arenaCreated);
                    if (arenaRoom is null)
                    {
                        _log($"{channel}:{remote} Arena random/specified room entry failed without creating a room: mode={quickMode} type={session.ArenaGameType} character={session.Character.Name}");
                        return BuildNativeFrame(frame, 0xCF78, BuildArenaQuickEnterFailurePayload(), session);
                    }
                    var arenaResult = arenaCreated ? (ushort)100 : (ushort)10;
                    QueueArenaLobbyRoomListRefresh(
                        session,
                        arenaCreated ? "arena automatic room created" : "arena quick member joined");
                    _log($"{channel}:{remote} Arena room entry accepted: mode={quickMode} result={arenaResult} room={session.ArenaRoomId} type={session.ArenaGameType} character={session.Character.Name}");
                    return BuildNativeFrame(
                        frame,
                        0xCF78,
                        BuildArenaQuickEnterPayload(arenaRoom, arenaResult),
                        session);
                }
                if (TryReuseDungeonTransitionQuickEntry(
                        session,
                        payload,
                        out var transitionQuickRoom,
                        out var transitionQuickRejection))
                {
                    _log($"{channel}:{remote} Dungeon transition CF77 reused retained room: room={transitionQuickRoom!.Id} action={transitionQuickRoom.SettlementAction} member={session.Character.Id} target={transitionQuickRoom.PendingEpisode}/{transitionQuickRoom.PendingDungeon}/{transitionQuickRoom.PendingStage} difficulty={transitionQuickRoom.Difficulty} members={transitionQuickRoom.Members.Count}");
                    return BuildNativeFrame(
                        frame,
                        0xCF78,
                        BuildDungeonQuickEnterPayload(transitionQuickRoom, 10),
                        session);
                }
                if (transitionQuickRejection is not null)
                {
                    _log($"{channel}:{remote} Rejected dungeon transition CF77 reentry: room={session.DungeonRoomId} character={session.Character.Id} reason={transitionQuickRejection}");
                    return null;
                }
                var quickSelectionAllowed = true;
                if (quickMode == 20)
                {
                    var requestedQuickRoom = GetDungeonRoomById(
                        BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2)));
                    quickSelectionAllowed = requestedQuickRoom is not null
                        && await CanCharacterEnterDungeonSelectionAsync(
                            session.Character,
                            requestedQuickRoom.Episode,
                            requestedQuickRoom.Dungeon,
                            requestedQuickRoom.BattleLogicalDifficulty,
                            token);
                }
                else
                {
                    var requestedQuickLogicalDifficulty = DecodeDungeonLogicalDifficulty(
                        payload[4],
                        payload[3],
                        payload[5]);
                    quickSelectionAllowed = await CanCharacterEnterDungeonSelectionAsync(
                        session.Character,
                        payload[2],
                        payload[4],
                        requestedQuickLogicalDifficulty,
                        token);
                    quickSelectionAllowed = quickSelectionAllowed
                        && DungeonCombatCatalog.HasStage(0, payload[2], payload[4], payload[3]);
                }
                var quickCreated = false;
                var quickRoom = quickSelectionAllowed
                    ? QuickEnterDungeonRoom(session, payload, out quickCreated)
                    : null;
                if (quickRoom is not null)
                    await RestoreDungeonVitalsAsync(session, false, token);
                var quickResult = quickRoom is null ? (ushort)50 : quickCreated ? (ushort)100 : (ushort)10;
                _log($"{channel}:{remote} 鍦板蹇€熷弬涓庡鐞嗭細mode={quickMode} " +
                     $"episode={payload[2]} dungeon={payload[4]} stage={payload[3]} level={payload[5]} " +
                     $"result={quickResult} room={quickRoom?.Id ?? 0} character={session.Character.Name}");
                return BuildNativeFrame(frame, 0xCF78,
                    BuildDungeonQuickEnterPayload(quickRoom, quickResult), session);
            }

            case 0xCF6E: // REQ_FLYSHOOTING_ENTER_GAMEROOM -> ANS_FLYSHOOTING_ENTER_GAMEROOM
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (!session.AuxiliaryGameSession
                            || payload.Length != 0
                            || !TryBuildEntertainmentWaitingRoomResponse(
                                session,
                                out var entertainmentWaitingPayload))
                            return null;
                        _log($"{channel}:{remote} Entertainment waiting room initialized: room={session.EntertainmentRoomId} member={session.Character.Name} slot={session.EntertainmentSlotIndex}");
                        return BuildNativeFrame(
                            frame,
                            0xCF6F,
                            entertainmentWaitingPayload,
                            session);
                    }
                    if (!session.AuxiliaryGameSession || payload.Length is not (0 or 6 or 8))
                        return null;
                    var enteredArenaRoom = GetArenaRoom(session);
                    if (enteredArenaRoom is null)
                        return BuildNativeFrame(frame, 0xCF6F, BuildDungeonGameplayEnterFailurePayload(), session);
                    _log($"{channel}:{remote} Arena gameplay-room context returned: room={enteredArenaRoom.Id} type={enteredArenaRoom.GameType} character={session.Character.Name} slot={session.ArenaSlotIndex}");
                    return BuildNativeFrame(
                        frame,
                        0xCF6F,
                        BuildArenaGameplayEnterPayload(enteredArenaRoom, session),
                        session);
                }
                if (payload.Length is not (6 or 8))
                    return null;
                var enterMode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                if (payload.Length == 8)
                {
                    var requestedRoomId = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                    if (session.DungeonRoomId == 0)
                    {
                        var requestedGameplayRoom = GetDungeonRoomById(requestedRoomId);
                        if (requestedGameplayRoom is null
                            || !await CanCharacterEnterDungeonSelectionAsync(
                                session.Character,
                                requestedGameplayRoom.Episode,
                                requestedGameplayRoom.Dungeon,
                                requestedGameplayRoom.BattleLogicalDifficulty,
                                token)
                            || !TryJoinDungeonRoom(session, requestedRoomId, out _))
                            return BuildNativeFrame(frame, 0xCF6F, BuildDungeonGameplayEnterFailurePayload(), session);
                    }
                }
                var gameplayRoom = GetDungeonRoom(session);
                if (gameplayRoom is null)
                    return BuildNativeFrame(frame, 0xCF6F, BuildDungeonGameplayEnterFailurePayload(), session);
                if (!await CanCharacterEnterDungeonSelectionAsync(
                        session.Character,
                        gameplayRoom.Episode,
                        gameplayRoom.Dungeon,
                        gameplayRoom.BattleLogicalDifficulty,
                        token))
                    return BuildNativeFrame(frame, 0xCF6F, BuildDungeonGameplayEnterFailurePayload(), session);
                await RestoreDungeonVitalsAsync(session, false, token);
                _log($"{channel}:{remote} 地宫游戏层进入成功：mode={enterMode} room={gameplayRoom.Id} character={session.Character.Id}");
                return BuildNativeFrame(
                    frame,
                    0xCF6F,
                    BuildDungeonGameplayEnterPayload(gameplayRoom, session),
                    session);
            }

            case 0xC587: // room-local identity request -> C588
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != 0)
                {
                    _log($"{channel}:{remote} 地宫房间本地身份请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }
                if (TryGetDungeonTransition(session, out var identityTransitionRoomId, out var identityTransitionAction))
                {
                    _log($"{channel}:{remote} Dungeon transition ignored early C587 room refresh: room={identityTransitionRoomId} action={identityTransitionAction} characterId={session.Character.Id}");
                }
                _log($"{channel}:{remote} 地宫房间本地身份已确认：characterId={session.Character.Id}");
                return BuildNativeFrame(
                    frame,
                    0xC588,
                    BuildGameRoomLocalIdentityPayload(session.Character),
                    session);

            case 0xCF70: // REQ_FLYSHOOTING_GAMEROOM_USER_INFO -> GAMEROOM_ENTER_USER
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != GameRoomUserInfoRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 地宫房间成员请求长度无效：期望 {GameRoomUserInfoRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                var roomUserInfoCursor = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        var entertainmentRoom = GetEntertainmentRoom(session);
                        var entertainmentOwner = FindEntertainmentRoomOwner(session);
                        if (!session.AuxiliaryGameSession
                            || entertainmentRoom is null
                            || entertainmentOwner?.Character is null)
                            return null;
                        _ = roomUserInfoCursor;
                        QueueEntertainmentMemberSnapshots(session);
                        return BuildNativeFrame(
                            frame,
                            0xCF71,
                            BuildGameRoomUserPayload(
                                session.Character,
                                entertainmentOwner.Character,
                                session.EntertainmentSlotIndex),
                            session);
                    }
                    if (!session.AuxiliaryGameSession)
                        return null;
                    var arenaRoomUser = FindArenaRoomMember(session, roomUserInfoCursor);
                    var arenaRoomOwner = FindArenaRoomOwner(session);
                    if (arenaRoomUser?.Character is null || arenaRoomOwner?.Character is null)
                    {
                        _log($"{channel}:{remote} Arena room member lookup failed: cursor={roomUserInfoCursor} room={session.ArenaRoomId}");
                        return null;
                    }

                    QueueArenaMemberSnapshots(session);
                    _log($"{channel}:{remote} Arena room member initialized: cursor={roomUserInfoCursor} room={session.ArenaRoomId} member={arenaRoomUser.Character.Id} owner={arenaRoomOwner.Character.Id} slot={arenaRoomUser.ArenaSlotIndex}");
                    return BuildNativeFrame(
                        frame,
                        0xCF71,
                        BuildGameRoomUserPayload(
                            arenaRoomUser.Character,
                            arenaRoomOwner.Character,
                            arenaRoomUser.ArenaSlotIndex),
                        session);
                }
                var roomUser = FindDungeonRoomMember(session, roomUserInfoCursor);
                var roomOwner = FindDungeonRoomOwner(session);
                if (roomUser?.Character is null || roomOwner?.Character is null)
                {
                    _log($"{channel}:{remote} 地宫房间成员查询无匹配项：cursor={roomUserInfoCursor} room={session.DungeonRoomId}");
                    return null;
                }
                if (TryGetDungeonTransition(session, out var userInfoTransitionRoomId, out var userInfoTransitionAction))
                {
                    _log($"{channel}:{remote} Dungeon transition ignored early CF70 room refresh: room={userInfoTransitionRoomId} action={userInfoTransitionAction} characterId={session.Character.Id}");
                }
                _log($"{channel}:{remote} 返回地宫房间成员：cursor={roomUserInfoCursor} member={roomUser.Character.Id} owner={roomOwner.Character.Id}");
                var readyRoom = GetDungeonRoom(session);
                if (readyRoom is null)
                    return null;
                var readyRoomRanks = await LoadDungeonReadyRoomRanksAsync(readyRoom, token);
                var roomUserReadyRank = readyRoomRanks.GetValueOrDefault(roomUser.Character.Id);
                QueueDungeonMemberSnapshots(session, readyRoomRanks);
                var roomUserSkillSlots = await GetEffectiveDungeonSkillSlotsAsync(roomUser, token);
                IReadOnlyList<CharacterSkillRecord> roomUserLearnedSkills = roomUser.AccountId > 0
                    ? await _database.GetCharacterSkillsAsync(roomUser.Character.Id, token)
                    : [];
                IReadOnlyList<CharacterCardRecord> roomUserCards = roomUser.AccountId > 0
                    ? await _database.GetCharacterCardsAsync(roomUser.Character.Id, token)
                    : [];
                var skillStateRequest = new byte[] { 0x28, 0x00, 0x03, 0x01 };
                var skillStateFrame = BuildNativeFrame(
                    frame,
                    0xC3E8,
                    BuildCardListPayload(
                        skillStateRequest,
                        roomUserCards,
                        roomUser.Character,
                        roomUserLearnedSkills),
                    session);
                var roomUserFrame = BuildNativeFrame(
                    frame,
                    0xCF71,
                    BuildGameRoomUserPayload(
                        roomUser.Character,
                        roomOwner.Character,
                        roomUser.DungeonSlotIndex,
                        roomUserSkillSlots.Skill0,
                        roomUserSkillSlots.Grade0,
                        roomUserSkillSlots.Skill1,
                        roomUserSkillSlots.Grade1,
                        roomUserReadyRank),
                    session);
                _log($"{channel}:{remote} Dungeon runtime skills restored before CF71/CFEC: " +
                     $"z={roomUserSkillSlots.Skill0}:{roomUserSkillSlots.Grade0} " +
                     $"x={roomUserSkillSlots.Skill1}:{roomUserSkillSlots.Grade1} " +
                     $"expansion={roomUser.Character.SkillSlotExpansionExpires}");
                return CombineNativeFrames(skillStateFrame, roomUserFrame);
            }

            case 0xCF7B: // REQ_SLOT_CHANGE -> ANS_SLOT_CHANGE
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != GameRoomSlotChangeRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 地宫房间位置切换请求长度无效：期望 {GameRoomSlotChangeRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                var slotIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var requestedState = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                if ((IsEntertainmentSession(channel, session)
                        && slotIndex >= EntertainmentProtocol.MaximumMembers)
                    || (!IsEntertainmentSession(channel, session) && slotIndex > 2))
                {
                    _log($"{channel}:{remote} 地宫房间位置索引无效：slot={slotIndex}；仅接受 1..3");
                    return null;
                }
                if (requestedState > byte.MaxValue)
                {
                    _log($"{channel}:{remote} 地宫房间位置状态无效：slot={slotIndex} state={requestedState}；仅接受 0 或 2");
                    return null;
                }
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (!session.AuxiliaryGameSession
                            || requestedState > 2
                            || !TrySetEntertainmentSlotState(
                                session,
                                (byte)slotIndex,
                                (byte)requestedState))
                            return null;
                        var entertainmentSlotPayload = DungeonProtocol.BuildSlotChange(
                            (byte)slotIndex,
                            (byte)requestedState);
                        QueueEntertainmentBroadcast(
                            session,
                            0xCF7C,
                            entertainmentSlotPayload,
                            false,
                            "entertainment slot state");
                        return BuildNativeFrame(
                            frame,
                            0xCF7C,
                            entertainmentSlotPayload,
                            session);
                    }
                    if (!session.AuxiliaryGameSession
                        || requestedState is not (0 or 2)
                        || !TrySetArenaSlotState(session, (byte)slotIndex, (byte)requestedState))
                    {
                        _log($"{channel}:{remote} Arena slot state rejected: room={session.ArenaRoomId} slot={slotIndex} state={requestedState}");
                        return null;
                    }

                    var arenaSlotPayload = DungeonProtocol.BuildSlotChange(
                        (byte)slotIndex,
                        (byte)requestedState);
                    QueueArenaBroadcast(
                        session,
                        0xCF7C,
                        arenaSlotPayload,
                        false,
                        "arena slot state");
                    _log($"{channel}:{remote} Arena slot state synchronized: room={session.ArenaRoomId} slot={slotIndex} state={requestedState}");
                    return BuildNativeFrame(frame, 0xCF7C, arenaSlotPayload, session);
                }
                if (!TrySetDungeonSlotState(session, slotIndex, (byte)requestedState))
                {
                    _log($"{channel}:{remote} 地宫槽位切换被拒绝：room={session.DungeonRoomId} slot={slotIndex}，仅房主可修改空槽位");
                    return null;
                }
                var slotChangePayload = DungeonProtocol.BuildSlotChange((byte)slotIndex, (byte)requestedState);
                QueueDungeonBroadcast(session, 0xCF7C, slotChangePayload, false, "dungeon slot change");
                _log($"{channel}:{remote} 地宫房间位置已同步：room={session.DungeonRoomId} slot={slotIndex} state={requestedState}");
                return BuildNativeFrame(
                    frame,
                    0xCF7C,
                    slotChangePayload,
                    session);

            case 0xCF7D: // REQ_GAME_READY -> ANS_GAME_READY
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != 4)
                    return null;
                var ready = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var teamCode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (!session.AuxiliaryGameSession
                            || ready > 1
                            || teamCode > byte.MaxValue
                            || !TrySetEntertainmentReady(session, ready != 0, (byte)teamCode))
                            return null;
                        var entertainmentReadyPayload = BuildGameReadyPayload(
                            session.Character,
                            ready != 0,
                            (byte)teamCode);
                        QueueEntertainmentBroadcast(
                            session,
                            0xCF7E,
                            entertainmentReadyPayload,
                            false,
                            "entertainment ready state");
                        return BuildNativeFrame(
                            frame,
                            0xCF7E,
                            entertainmentReadyPayload,
                            session);
                    }
                    if (!session.AuxiliaryGameSession
                        || ready > 1
                        || teamCode > 2
                        || !TrySetArenaReady(session, ready != 0, (byte)teamCode))
                    {
                        _log($"{channel}:{remote} Arena ready state rejected: room={session.ArenaRoomId} ready={ready} team={teamCode}");
                        return null;
                    }
                    var arenaReadyPayload = BuildGameReadyPayload(
                        session.Character, ready != 0, (byte)teamCode);
                    QueueArenaBroadcast(session, 0xCF7E, arenaReadyPayload, false, "arena ready state");
                    _log($"{channel}:{remote} Arena ready state synchronized: room={session.ArenaRoomId} uid={session.Character.Id} ready={ready} team={teamCode}");
                    return BuildNativeFrame(frame, 0xCF7E, arenaReadyPayload, session);
                }
                if (ready > 1 || teamCode > byte.MaxValue
                    || !TrySetDungeonReady(session, ready != 0, (byte)teamCode))
                {
                    _log($"{channel}:{remote} 鍦板鍑嗗鐘舵€佹棤鏁堬細room={session.DungeonRoomId} ready={ready} team={teamCode}");
                    return null;
                }
                var readyPayload = DungeonProtocol.BuildReady(
                    checked((ushort)session.Character.Id), ready != 0, (byte)teamCode);
                QueueDungeonBroadcast(session, 0xCF7E, readyPayload, false, "dungeon ready state");
                _log($"{channel}:{remote} 鍦板鍑嗗鐘舵€佸凡鍚屾锛歳oom={session.DungeonRoomId} uid={session.Character.Id} ready={ready} team={teamCode}");
                return BuildNativeFrame(frame, 0xCF7E, readyPayload, session);
            }

            case 0xCFD1: // REQ_P2P_MY_INFO -> ANS_P2P_MY_INFO
            {
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未建立世界会话的 P2P 信息请求");
                    return null;
                }
                if (payload.Length != P2PMyInfoRequestPayloadLength)
                {
                    _log($"{channel}:{remote} P2P 信息请求长度无效：期望 {P2PMyInfoRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                var isArenaP2P = string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal);
                var isEntertainmentP2P = IsEntertainmentSession(channel, session);
                if ((isEntertainmentP2P
                        ? GetEntertainmentRoom(session) is null
                        : isArenaP2P ? GetArenaRoom(session) is null : GetDungeonRoom(session) is null)
                    || !TryParseP2PMyInfo(payload, out var p2pIpAddress, out var p2pPort))
                {
                    _log($"{channel}:{remote} P2P 端点无效或角色不在当前游戏房间；未响应且未注册端点");
                    return null;
                }
                session.P2PIpAddress = p2pIpAddress;
                session.P2PPort = p2pPort;
                session.P2PInfoRegistered = true;
                var p2pSlotIndex = isEntertainmentP2P
                    ? session.EntertainmentSlotIndex
                    : isArenaP2P ? session.ArenaSlotIndex : session.DungeonSlotIndex;
                _log($"{channel}:{remote} 本地 P2P 身份初始化完成：characterId={session.Character.Id} slot={p2pSlotIndex} endpoint={p2pIpAddress}:{p2pPort} scene={(isArenaP2P ? "arena" : "dungeon")}");
                return BuildNativeFrame(
                    frame,
                    0xCFD2,
                    BuildP2PMyInfoPayload(session.Character, p2pSlotIndex),
                    session);
            }

            case 0xCFD3: // REQ_P2P_OTHER_INFO -> ANS_P2P_OTHER_INFO
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != 0)
                {
                    _log($"{channel}:{remote} P2P 其他成员请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }
                var arenaPeerRequest = string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal);
                var entertainmentPeerRequest = IsEntertainmentSession(channel, session);
                var (expectedPeerCount, peers) = entertainmentPeerRequest
                    ? GetEntertainmentP2PPeers(session)
                    : arenaPeerRequest ? GetArenaP2PPeers(session)
                    : GetDungeonP2PPeers(session);
                for (var attempt = 0; peers.Length < expectedPeerCount && attempt < 40; attempt++)
                {
                    await Task.Delay(50, token);
                    (expectedPeerCount, peers) = entertainmentPeerRequest
                        ? GetEntertainmentP2PPeers(session)
                        : arenaPeerRequest ? GetArenaP2PPeers(session)
                        : GetDungeonP2PPeers(session);
                }
                _log($"{channel}:{remote} P2P 其他成员列表已初始化：scene={(arenaPeerRequest ? "arena" : "dungeon")} room={(arenaPeerRequest ? session.ArenaRoomId : session.DungeonRoomId)} count={peers.Length} expected={expectedPeerCount}");
                return BuildNativeFrame(frame, 0xCFD4, BuildP2POtherInfoPayload(peers), session);
            }

            case 0xCFD5: // REQ_P2P_PROTOCOL -> ANS_P2P_PROTOCOL
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != P2PProtocolRequestPayloadLength)
                {
                    _log($"{channel}:{remote} P2P 协议请求长度无效：期望 {P2PProtocolRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                var requestedP2PProtocol = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                var arenaProtocolRequest = string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal);
                if (arenaProtocolRequest)
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (!session.AuxiliaryGameSession || GetEntertainmentRoom(session) is null)
                            return null;
                        session.EntertainmentP2PProtocolConfirmed = true;
                    }
                    else if (!session.AuxiliaryGameSession || GetArenaRoom(session) is null)
                    {
                        _log($"{channel}:{remote} Arena P2P protocol rejected outside a room: character={session.Character.Id}");
                        return null;
                    }
                    else
                    {
                        session.ArenaP2PProtocolConfirmed = true;
                    }
                }
                _log($"{channel}:{remote} P2P 协议已确认：scene={(string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal) ? "arena" : "dungeon")} room={(string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal) ? session.ArenaRoomId : session.DungeonRoomId)} character={session.Character.Id} slot={(string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal) ? session.ArenaSlotIndex : session.DungeonSlotIndex)} requested={requestedP2PProtocol} protocol=20");
                return BuildNativeFrame(frame, 0xCFD6, BuildP2PProtocolPayload(), session);

            case 0xCFD9: // REQ_MULTICASTING_GAMEEVENT -> ANS_MULTICASTING_GAMEEVENT
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未建立世界会话的组播初始化请求");
                    return null;
                }
                if (payload.Length != 0)
                {
                    _log($"{channel}:{remote} 组播初始化请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }
                _log($"{channel}:{remote} 本地组播身份初始化完成：characterId={session.Character.Id}");
                var multicastPayload = BuildMulticastingGameEventPayload(session.Character);
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (!session.AuxiliaryGameSession || GetEntertainmentRoom(session) is null)
                            return null;
                        var firstEntertainmentMulticast = !session.EntertainmentMulticastInitialized;
                        session.EntertainmentMulticastInitialized = true;
                        if (firstEntertainmentMulticast)
                            QueueEntertainmentBroadcast(
                                session,
                                0xCFDA,
                                multicastPayload,
                                false,
                                "entertainment member entity activation");
                    }
                    else if (!session.AuxiliaryGameSession || GetArenaRoom(session) is null)
                        return null;
                    else
                    {
                        var firstArenaMulticastInitialization = !session.ArenaMulticastInitialized;
                        session.ArenaMulticastInitialized = true;
                        if (firstArenaMulticastInitialization)
                        {
                            QueueArenaBroadcast(
                                session,
                                0xCFDA,
                                multicastPayload,
                                false,
                                "arena member entity activation");
                        }
                    }
                }
                else
                {
                    if (GetDungeonRoom(session) is null)
                        return null;
                    var firstDungeonMulticastInitialization = !session.DungeonMulticastInitialized;
                    session.DungeonMulticastInitialized = true;
                    if (firstDungeonMulticastInitialization)
                    {
                        QueueDungeonBroadcast(
                            session,
                            0xCFDA,
                            multicastPayload,
                            false,
                            "dungeon member entity activation");
                    }
                }
                return BuildNativeFrame(frame, 0xCFDA, multicastPayload, session);

            case 0xCFEB: // REQ_FLYSHOOTING_GAMEDATA -> ANS_FLYSHOOTING_GAMEDATA
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未建立世界会话的地宫游戏数据请求");
                    return null;
                }
                if (payload.Length != FlyshootingGameDataRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 地宫游戏数据请求长度无效：期望 {FlyshootingGameDataRequestPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                var requestedStage = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var requestedMapIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                        return null;
                    var arenaGameDataRoom = GetArenaRoom(session);
                    // The retail client requests CFEC while it is still in the room's
                    // pre-start loading path. CFEC clears the client's pending-data
                    // flag; CF7F/CF80 follows as the separate authoritative start.
                    if (!session.AuxiliaryGameSession || arenaGameDataRoom is null)
                    {
                        _log($"{channel}:{remote} Arena game-data request rejected: room={session.ArenaRoomId} character={session.Character.Name}");
                        return null;
                    }

                    byte[] arenaGameData;
                    ushort resolvedStage;
                    ushort resolvedMapIndex;
                    lock (_arenaRoomGate)
                    {
                        if (arenaGameDataRoom.GameDataPayload.Length == ArenaProtocol.GameDataResponseLength)
                        {
                            arenaGameData = arenaGameDataRoom.GameDataPayload.ToArray();
                            resolvedStage = arenaGameData[0x2D2];
                            resolvedMapIndex = arenaGameData[0x2D3];
                        }
                        else
                        {
                            (resolvedStage, resolvedMapIndex) = ArenaProtocol.ResolveGameSelectors(
                                requestedStage,
                                requestedMapIndex);
                            arenaGameData = ArenaProtocol.BuildGameData(
                                resolvedStage,
                                resolvedMapIndex);
                            arenaGameDataRoom.SelectedMode = resolvedStage;
                            arenaGameDataRoom.SelectedMap = resolvedMapIndex;
                            arenaGameDataRoom.GameDataPayload = arenaGameData.ToArray();
                        }
                    }
                    QueueArenaBroadcast(
                        session,
                        0xCFEC,
                        arenaGameData,
                        false,
                        "arena game data before start");
                    _log($"{channel}:{remote} Arena game data synchronized before start: room={arenaGameDataRoom.Id} started={arenaGameDataRoom.Started} requested={requestedStage}/{requestedMapIndex} resolved={resolvedStage}/{resolvedMapIndex} character={session.Character.Name}");
                    return BuildNativeFrame(frame, 0xCFEC, arenaGameData, session);
                }
                _log($"{channel}:{remote} 鍗曚汉鍦板寮€濮嬫父鎴忥細room={session.TownPage} characterId={session.Character.Id}");
                var gameDataRoom = GetDungeonRoom(session);
                if (gameDataRoom is null)
                    return null;
                var gameSkillRecords = await GetDungeonGameSkillRecordsAsync(gameDataRoom, token);
                byte gameDataEpisode;
                byte gameDataDungeon;
                byte gameDataStage;
                ushort selectedMapIndex;
                byte[] gameDataPayload;
                lock (_dungeonRoomGate)
                {
                    if (!_dungeonRooms.TryGetValue(session.DungeonRoomId, out var currentGameDataRoom)
                        || !ReferenceEquals(currentGameDataRoom, gameDataRoom)
                        || !gameDataRoom.Members.ContainsKey(session.SessionId))
                    {
                        _log($"{channel}:{remote} Rejected dungeon game-data request after room changed during skill lookup: room={session.DungeonRoomId} character={session.Character.Id}");
                        return null;
                    }
                    gameDataEpisode = gameDataRoom.HasPendingTransition
                        ? gameDataRoom.PendingEpisode
                        : gameDataRoom.BattleEpisode;
                    gameDataDungeon = gameDataRoom.HasPendingTransition
                        ? gameDataRoom.PendingDungeon
                        : gameDataRoom.BattleDungeon;
                    gameDataStage = gameDataRoom.HasPendingTransition
                        ? gameDataRoom.PendingStage
                        : gameDataRoom.BattleStage;
                    // CF6C byte 29 and CFEB StageIdx both originate from the
                    // client's RealStage global (+0x1E540). CF6C word 30 is the
                    // independent difficulty column (+0x1E528). Never load a
                    // resource stage that diverges from the room selection.
                    if (requestedStage != gameDataStage
                        || !DungeonCombatCatalog.HasStage(
                            gameDataRoom.HdIndex,
                            gameDataEpisode,
                            gameDataDungeon,
                            gameDataStage)
                        || gameDataRoom.HasPendingTransition
                            && (requestedStage != gameDataRoom.Battle.RequestedStageIndex
                                || requestedMapIndex != gameDataRoom.Battle.ShowStageNumber))
                    {
                        _log($"{channel}:{remote} Rejected stale/unavailable dungeon game-data selectors: room={gameDataRoom.Id} requested={requestedStage}/{requestedMapIndex} resourceStage={gameDataStage} expected={gameDataRoom.Battle.RequestedStageIndex}/{gameDataRoom.Battle.ShowStageNumber} action={gameDataRoom.SettlementAction}");
                        return null;
                    }
                    var upgradeDropPlan = GetOrCreateDungeonUpgradeDropPlanLocked(gameDataRoom);
                    var inDungeonItemDropPlan = GetOrCreateDungeonItemDropPlanLocked(gameDataRoom);
                    var (randomPlanA, randomPlanB) = GetOrCreateDungeonRandomPlansLocked(gameDataRoom);
                    selectedMapIndex = GetOrCreateDungeonMapIndexLocked(gameDataRoom);
                    gameDataPayload = DungeonProtocol.BuildGameData(
                        requestedStage,
                        requestedMapIndex,
                        selectedMapIndex,
                        randomPlanA,
                        randomPlanB,
                        upgradeDropPlan,
                        inDungeonItemDropPlan);
                    DungeonProtocol.WriteGameSkillRecords(gameDataPayload, gameSkillRecords);
                    gameDataRoom.GameDataPayloadsBySession[session.SessionId] = gameDataPayload.ToArray();
                    gameDataRoom.Battle.RequestedStageIndex = requestedStage;
                    gameDataRoom.Battle.ShowStageNumber = requestedMapIndex;
                }
                if (TryGetDungeonTransition(session, out var gameDataTransitionRoomId, out var gameDataTransitionAction))
                {
                    MarkDungeonTransitionReentered(session, allowDirectGameData: true);
                    _log($"{channel}:{remote} Dungeon transition target game data confirmed: room={gameDataTransitionRoomId} action={gameDataTransitionAction} characterId={session.Character.Id}");
                }
                var cardDropCount = gameDataPayload.AsSpan(0x26E, 50).ToArray().Count(value => value != 0);
                _log($"{channel}:{remote} Dungeon game data selected: room={gameDataRoom.Id} character={session.Character.Id} episode={gameDataEpisode} dungeon={gameDataDungeon} realStage={gameDataStage} requestedStageIdx={requestedStage} requestedShowStage={requestedMapIndex} selectedMapIdx={selectedMapIndex} members={gameDataRoom.Members.Count} cardDrops={cardDropCount}/50 pending={gameDataRoom.HasPendingTransition}");
                return BuildNativeFrame(frame, 0xCFEC,
                    gameDataPayload,
                    session);

            case 0xCFD7: // REQ_DROPOUT_BAD_NETWORK_PLAYER -> ANS_DROPOUT_BAD_NETWORK_PLAYER
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != 4)
                    return null;
                var droppedUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        var droppedEntertainmentMember = FindEntertainmentMemberByUid(session, droppedUid);
                        if (droppedUid == 0
                            || droppedEntertainmentMember?.Character is null
                            || droppedEntertainmentMember.SessionId == session.SessionId)
                            return null;
                        var entertainmentOwnerAfterDrop = FindEntertainmentOwnerAfterLeave(
                            droppedEntertainmentMember) ?? droppedEntertainmentMember.Character;
                        var entertainmentDropoutPayload = new byte[4];
                        BinaryPrimitives.WriteUInt16LittleEndian(entertainmentDropoutPayload.AsSpan(0, 2), droppedUid);
                        BinaryPrimitives.WriteUInt16LittleEndian(
                            entertainmentDropoutPayload.AsSpan(2, 2),
                            GetSceneEntityId(entertainmentOwnerAfterDrop));
                        QueueEntertainmentBroadcast(
                            session,
                            0xCFD8,
                            entertainmentDropoutPayload,
                            false,
                            "entertainment bad-network member dropout");
                        RemoveEntertainmentRoomMember(droppedEntertainmentMember, session);
                        QueueEntertainmentLobbyRoomListRefresh(session, "entertainment member dropout");
                        return BuildNativeFrame(frame, 0xCFD8, entertainmentDropoutPayload, session);
                    }
                    var droppedArenaMember = FindArenaRoomMemberByUid(session, droppedUid);
                    if (!session.AuxiliaryGameSession
                        || droppedUid == 0
                        || droppedArenaMember?.Character is null
                        || droppedArenaMember.SessionId == session.SessionId)
                    {
                        _log($"{channel}:{remote} Ignored stale arena P2P dropout: room={session.ArenaRoomId} reporter={session.Character.Id} dropped={droppedUid}");
                        return null;
                    }

                    var arenaOwnerAfterDrop = FindArenaRoomOwnerAfterLeave(droppedArenaMember)
                        ?? droppedArenaMember.Character;
                    var arenaDropoutPayload = new byte[4];
                    BinaryPrimitives.WriteUInt16LittleEndian(arenaDropoutPayload.AsSpan(0, 2), droppedUid);
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        arenaDropoutPayload.AsSpan(2, 2),
                        GetSceneEntityId(arenaOwnerAfterDrop));
                    QueueArenaBroadcast(
                        session,
                        0xCFD8,
                        arenaDropoutPayload,
                        false,
                        "arena bad-network member dropout");
                    RemoveArenaRoomMember(droppedArenaMember, session);
                    QueueArenaLobbyRoomListRefresh(session, "arena bad-network member removed");
                    _log($"{channel}:{remote} Arena P2P dropout synchronized: room={session.ArenaRoomId} reporter={session.Character.Id} dropped={droppedUid} owner={arenaOwnerAfterDrop.Id}");
                    return BuildNativeFrame(frame, 0xCFD8, arenaDropoutPayload, session);
                }
                var dropoutRoom = GetDungeonRoom(session);
                var dropoutOwner = FindDungeonRoomOwner(session)?.Character;
                var droppedMemberExists = dropoutRoom?.Members.Values.Any(member =>
                    member.Session.SessionId != session.SessionId
                    && member.Session.Character is { } character
                    && GetSceneEntityId(character) == droppedUid) == true;
                if (dropoutRoom is null || dropoutOwner is null || droppedUid == 0 || !droppedMemberExists)
                {
                    _log($"{channel}:{remote} Ignored stale dungeon P2P dropout: room={session.DungeonRoomId} reporter={session.Character.Id} dropped={droppedUid}");
                    return null;
                }
                var dropoutPayload = new byte[4];
                BinaryPrimitives.WriteUInt16LittleEndian(dropoutPayload.AsSpan(0, 2), droppedUid);
                BinaryPrimitives.WriteUInt16LittleEndian(dropoutPayload.AsSpan(2, 2), GetSceneEntityId(dropoutOwner));
                QueueDungeonBroadcast(session, 0xCFD8, dropoutPayload, false, "dungeon bad-network member dropout");
                _log($"{channel}:{remote} Dungeon P2P dropout acknowledged: room={dropoutRoom.Id} reporter={session.Character.Id} dropped={droppedUid} owner={dropoutOwner.Id}");
                return BuildNativeFrame(frame, 0xCFD8, dropoutPayload, session);
            }

            case 0x0514: // dungeon peer trigger; same opcode is relayed to peers
            {
                var arenaPeerRelay = string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal);
                var entertainmentPeerRelay = IsEntertainmentSession(channel, session);
                if (!session.OnlineTracked
                    || session.Character is null
                    || (entertainmentPeerRelay
                        ? GetEntertainmentRoom(session) is null
                        : arenaPeerRelay ? GetArenaRoom(session) is null : GetDungeonRoom(session) is null)
                    || payload.Length != DungeonPeerTriggerPayloadLength)
                    return null;

                var sourceEntityId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                var expectedEntityId = GetSceneEntityId(session.Character);
                if (sourceEntityId != expectedEntityId)
                {
                    _log($"{channel}:{remote} rejected {(arenaPeerRelay ? "arena" : "dungeon")} peer trigger identity: room={(arenaPeerRelay ? session.ArenaRoomId : session.DungeonRoomId)} source={sourceEntityId} expected={expectedEntityId}");
                    return null;
                }

                if (entertainmentPeerRelay)
                    QueueEntertainmentBroadcast(session, 0x0514, payload, false, "entertainment peer trigger");
                else if (arenaPeerRelay)
                    QueueArenaBroadcast(session, 0x0514, payload, false, "arena peer trigger");
                else
                    QueueDungeonBroadcast(session, 0x0514, payload, false, "dungeon peer trigger");
                _log($"{channel}:{remote} {(arenaPeerRelay ? "arena" : "dungeon")} peer trigger relayed: room={(arenaPeerRelay ? session.ArenaRoomId : session.DungeonRoomId)} source={sourceEntityId}");
                return null;
            }

            case 0x0578: // P2P room heartbeat / loading progress
            case 0x03E8: // P2P flyshooting player state
            {
                var arenaPeerRelay = string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal);
                var entertainmentPeerRelay = IsEntertainmentSession(channel, session);
                if (!session.OnlineTracked
                    || session.Character is null
                    || (entertainmentPeerRelay
                        ? GetEntertainmentRoom(session) is null
                        : arenaPeerRelay ? GetArenaRoom(session) is null : GetDungeonRoom(session) is null))
                    return null;
                var expectedLength = opcode == 0x0578
                    ? DungeonPeerHeartbeatPayloadLength
                    : DungeonPeerStatePayloadLength;
                if (payload.Length != expectedLength
                    || BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) != session.Character.Id)
                {
                    _log($"{channel}:{remote} Rejected {(arenaPeerRelay ? "arena" : "dungeon")} peer relay: opcode=0x{opcode:X4} expected={expectedLength} actual={payload.Length} character={session.Character.Id}");
                    return null;
                }
                if (entertainmentPeerRelay)
                {
                    QueueEntertainmentBroadcast(
                        session,
                        opcode,
                        payload,
                        false,
                        opcode == 0x0578
                            ? "entertainment peer loading heartbeat"
                            : "entertainment peer player state");
                }
                else if (arenaPeerRelay)
                {
                    QueueArenaBroadcast(
                        session,
                        opcode,
                        payload,
                        false,
                        opcode == 0x0578 ? "arena peer loading heartbeat" : "arena peer player state");
                }
                else
                {
                    QueueDungeonBroadcast(
                        session,
                        opcode,
                        payload,
                        false,
                        opcode == 0x0578 ? "dungeon peer loading heartbeat" : "dungeon peer player state");
                }
                return null;
            }

            case 0x05DC: // P2P dungeon entity state 6
            case 0x0640: // P2P dungeon entity state 5
            {
                var arenaPeerRelay = string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal);
                var entertainmentPeerRelay = IsEntertainmentSession(channel, session);
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != DungeonPeerEntityEventPayloadLength)
                {
                    _log($"{channel}:{remote} Rejected dungeon entity event length: opcode=0x{opcode:X4} expected={DungeonPeerEntityEventPayloadLength} actual={payload.Length}");
                    return null;
                }

                var sourceUid = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var targetUid = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                if (sourceUid != GetSceneEntityId(session.Character)
                    || !(entertainmentPeerRelay
                        ? IsEntertainmentEntityInRoom(session, targetUid)
                        : arenaPeerRelay ? IsArenaEntityInRoom(session, targetUid)
                        : IsDungeonEntityInRoom(session, targetUid)))
                {
                    _log($"{channel}:{remote} Rejected {(arenaPeerRelay ? "arena" : "dungeon")} entity event identity: opcode=0x{opcode:X4} room={(arenaPeerRelay ? session.ArenaRoomId : session.DungeonRoomId)} source={sourceUid} expected={GetSceneEntityId(session.Character)} target={targetUid}");
                    return null;
                }

                var entityReason = opcode == 0x0640
                    ? $"{(arenaPeerRelay ? "arena" : "dungeon")} peer entity state 5"
                    : $"{(arenaPeerRelay ? "arena" : "dungeon")} peer entity state 6";
                if (entertainmentPeerRelay)
                    QueueEntertainmentBroadcast(session, opcode, payload, false, entityReason);
                else if (arenaPeerRelay)
                    QueueArenaBroadcast(session, opcode, payload, false, entityReason);
                else
                    QueueDungeonBroadcast(session, opcode, payload, false, entityReason);
                _log($"{channel}:{remote} {(arenaPeerRelay ? "Arena" : "Dungeon")} entity event relayed: opcode=0x{opcode:X4} room={(arenaPeerRelay ? session.ArenaRoomId : session.DungeonRoomId)} source={sourceUid} target={targetUid}");
                return null;
            }

            case 0x044C: // P2P shooting synchronization
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != ShootingSyncPayloadLength)
                {
                    _log($"{channel}:{remote} 射击同步长度无效：期望 {ShootingSyncPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }
                var arenaShooting = string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal);
                if (arenaShooting)
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (GetEntertainmentRoom(session) is null)
                            return null;
                        QueueEntertainmentBroadcast(session, 0x044C, payload, false, "entertainment shooting state");
                    }
                    else if (GetArenaRoom(session) is null)
                        return null;
                    else
                        QueueArenaBroadcast(session, 0x044C, payload, false, "arena shooting state");
                }
                else
                {
                    var dungeonShootingRoom = GetDungeonRoom(session);
                    // Retail sub_6ACBF0 writes the shooting runtime object's UID at
                    // payload+6. The 0x044C constructor initializes it to 0xFFFF;
                    // live projectiles then use dynamic values, so it is not a player UID.
                    var shootingRuntimeUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                    bool validDungeonShooting;
                    lock (_dungeonRoomGate)
                    {
                        validDungeonShooting = dungeonShootingRoom is not null
                            && dungeonShootingRoom.Battle.State == DungeonBattleState.Active
                            && dungeonShootingRoom.Members.ContainsKey(session.SessionId);
                        if (validDungeonShooting)
                        {
                            session.LastReportedPositionX = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10, 2));
                            session.LastReportedPositionY = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12, 2));
                            session.HasReportedDungeonPosition = true;
                        }
                    }
                    if (!validDungeonShooting)
                    {
                        _log($"{channel}:{remote} Rejected dungeon shooting state: room={session.DungeonRoomId} runtimeUid={shootingRuntimeUid} state={dungeonShootingRoom?.Battle.State.ToString() ?? "none"}");
                        return null;
                    }
                    QueueDungeonBroadcast(session, 0x044C, payload, false, "dungeon shooting state");
                }
                _log($"{channel}:{remote} {(arenaShooting ? "Arena" : "Dungeon")} shooting state synchronized: room={(arenaShooting ? session.ArenaRoomId : session.DungeonRoomId)} uid={BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2))} action={BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2))} position=({BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10, 2))},{BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12, 2))})");
                return null;

            case 0xD00D: // dungeon NPC collision / hit report
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != DungeonCollisionPayloadLength)
                {
                    _log($"{channel}:{remote} 地宫碰撞上报长度无效：期望 {DungeonCollisionPayloadLength}，实际 {payload.Length}；未响应");
                    return null;
                }

                var mode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var secondary = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                // Retail sub_6E9D40 selects this word from collision-object runtime
                // fields supplied by sub_670E10. Captures contain 1202/1204; it is
                // not the authenticated player's scene entity ID.
                var collisionSourceCode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
                // Retail sub_670E10 passes its target-vector loop index here.
                // Projectile type is read locally from projectile+4 and is not
                // serialized by sub_6E9D40.
                var targetIndex = payload[6];
                var flags = payload[7];
                var npcRuntimeUid = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
                var npcTargetKey = new DungeonNpcKey(npcRuntimeUid, targetIndex);
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                        return null;
                    var arenaCollisionRoom = GetArenaRoom(session);
                    bool firstRemoval;
                    lock (_arenaRoomGate)
                    {
                        if (arenaCollisionRoom is null
                            || !arenaCollisionRoom.Started
                            || !arenaCollisionRoom.Members.ContainsKey(session.SessionId)
                            || npcRuntimeUid > ushort.MaxValue)
                            return null;
                        firstRemoval = arenaCollisionRoom.ClearedEntityRuntimeUids.Add(npcRuntimeUid);
                    }
                    var arenaCollisionPayload = DungeonProtocol.BuildCollision(
                        [0u, 0u, 0u],
                        npcRuntimeUid,
                        defeated: true);
                    QueueArenaBroadcast(
                        session,
                        0xD00E,
                        arenaCollisionPayload,
                        false,
                        "arena entity collision removal");
                    _log($"{channel}:{remote} Arena entity removed: room={arenaCollisionRoom.Id} runtime={npcRuntimeUid} first={firstRemoval}");
                    return BuildNativeFrame(frame, 0xD00E, arenaCollisionPayload, session);
                }
                var collisionRoom = GetDungeonRoom(session);
                var collisionBattle = collisionRoom?.Battle;
                if (collisionRoom is null
                    || collisionBattle is null
                    || !collisionRoom.Started
                    || npcRuntimeUid >= ushort.MaxValue
                    || collisionRoom.SelectedMapIndex is not { } mapIndex)
                {
                    _log($"{channel}:{remote} Rejected dungeon projectile collision state/runtime: room={collisionRoom?.Id ?? 0} source={collisionSourceCode} map={collisionRoom?.SelectedMapIndex?.ToString() ?? "none"} runtime={npcRuntimeUid}");
                    return null;
                }

                if (!DungeonCombatCatalog.TryGetRuntime(
                        collisionRoom.HdIndex,
                        collisionRoom.BattleEpisode,
                        collisionRoom.BattleDungeon,
                        collisionRoom.BattleStage,
                        mapIndex,
                        npcRuntimeUid,
                        out var npcResourceUid,
                        out var combatTemplate))
                {
                    // Runtime UID zero is a real component in several official
                    // SMMO schedules. It is valid only when the expanded MMO
                    // catalog resolves it; an uncatalogued zero remains null.
                    if (npcRuntimeUid == 0)
                    {
                        _log($"{channel}:{remote} Rejected null dungeon projectile runtime: room={collisionRoom.Id} source={collisionSourceCode} map={mapIndex}");
                        return null;
                    }

                    uint[] uncataloguedScores;
                    bool firstRemoval;
                    lock (_dungeonRoomGate)
                    {
                        if (!ReferenceEquals(collisionRoom.Battle, collisionBattle)
                            || collisionBattle.State != DungeonBattleState.Active
                            || !collisionRoom.Members.ContainsKey(session.SessionId)
                            || collisionBattle.DeadCharacters.Contains(session.Character.Id))
                        {
                            _log($"{channel}:{remote} Rejected uncatalogued dungeon collision after battle-state validation: room={collisionRoom.Id} character={session.Character.Id} sameInstance={ReferenceEquals(collisionRoom.Battle, collisionBattle)} state={collisionBattle.State} dead={collisionBattle.DeadCharacters.Contains(session.Character.Id)}");
                            return null;
                        }

                        // Retail D00D carries only the live entity UID. Entities created
                        // after the preallocated SMMO component range have no static MMO
                        // tuple. D00E uses that live UID directly with removal marker 200.
                        firstRemoval = collisionBattle.DefeatedUncataloguedRuntimeUids.Add(
                            npcTargetKey);
                        uncataloguedScores = BuildDungeonCollisionScoresLocked(collisionRoom);
                    }

                    var uncataloguedPayload = DungeonProtocol.BuildCollision(
                        uncataloguedScores,
                        npcRuntimeUid,
                        defeated: true);
                    QueueDungeonBroadcast(
                        session,
                        0xD00E,
                        uncataloguedPayload,
                        false,
                        "dungeon dynamic/event entity collision");
                    _log($"{channel}:{remote} Dungeon dynamic/event entity removed: room={collisionRoom.Id} map={mapIndex} source={collisionSourceCode} targetIndex={targetIndex} flags=0x{flags:X2} runtime={npcRuntimeUid} first={firstRemoval} scoreReward=0 sceneDrop=0 scores={string.Join(',', uncataloguedScores)}");
                    return BuildNativeFrame(frame, 0xD00E, uncataloguedPayload, session);
                }

                var petState = PetProgression.GetState(
                    session.Character,
                    GetEquippedPetItemCode(session.Character));
                var attackProfile = PetProgression.GetAttackProfile(petState);
                var cardDropBonusPercent = PetProgression.GetCardDropBonusPercent(petState);
                var normalBaseAttack = attackProfile.GetForCategory(combatTemplate.AttackCategory);
                var effectiveDefense = (flags & 0x80) != 0
                    ? checked((int)(combatTemplate.Defense * 0.5))
                    : combatTemplate.Defense;
                uint[] collisionScores;
                int baseAttack;
                uint activeSkillCode;
                byte activeSkillGrade;
                int damage;
                int remainingHp;
                bool defeated;
                uint normalDropCardCode = 0;
                lock (_dungeonRoomGate)
                {
                    if (!ReferenceEquals(collisionRoom.Battle, collisionBattle)
                        || collisionBattle.State != DungeonBattleState.Active
                        || !collisionRoom.Members.ContainsKey(session.SessionId)
                        || collisionBattle.DeadCharacters.Contains(session.Character.Id))
                    {
                        _log($"{channel}:{remote} Rejected dungeon projectile collision after battle-state validation: room={collisionRoom.Id} character={session.Character.Id} sameInstance={ReferenceEquals(collisionRoom.Battle, collisionBattle)} state={collisionBattle.State} dead={collisionBattle.DeadCharacters.Contains(session.Character.Id)}");
                        return null;
                    }

                    baseAttack = GetDungeonBaseAttackLocked(
                        collisionBattle,
                        session.Character.Id,
                        normalBaseAttack,
                        Environment.TickCount64,
                        out activeSkillCode,
                        out activeSkillGrade);
                    var effectiveAttack = (flags & 0x40) != 0
                        ? checked((int)(baseAttack * 1.5))
                        : baseAttack;

                    if (!collisionRoom.Npcs.TryGetValue(npcTargetKey, out var npcState))
                    {
                        npcState = new DungeonNpcState(combatTemplate);
                        collisionRoom.Npcs.Add(npcTargetKey, npcState);
                    }

                    if (npcState.Template != combatTemplate)
                    {
                        _log($"{channel}:{remote} Rejected dungeon NPC template mutation: room={collisionRoom.Id} map={mapIndex} runtime={npcRuntimeUid} resource={npcResourceUid}");
                        return null;
                    }

                    var wasDefeated = npcState.Defeated;
                    damage = wasDefeated
                        ? 0
                        : Math.Min(
                            effectiveAttack > effectiveDefense
                                ? effectiveAttack - effectiveDefense
                                : 1,
                            npcState.CurrentHp);
                    npcState.CurrentHp -= damage;
                    defeated = npcState.Defeated;
                    remainingHp = npcState.CurrentHp;
                    if (defeated && !wasDefeated)
                    {
                        collisionRoom.HitScores[session.Character.Id] = checked(
                            collisionRoom.HitScores.GetValueOrDefault(session.Character.Id)
                            + combatTemplate.Score);
                        if (CardCatalog.TryRollDungeonDrop(
                                collisionRoom.BattleEpisode,
                                combatTemplate.ResourceCode,
                                cardDropBonusPercent,
                                out var droppedCard))
                        {
                            RegisterGeneratedDungeonCardLocked(
                                collisionRoom,
                                droppedCard.CardCode);
                            normalDropCardCode = droppedCard.CardCode;
                        }
                    }
                    collisionScores = BuildDungeonCollisionScoresLocked(collisionRoom);
                }
                var collisionResponsePayload = DungeonProtocol.BuildCollision(
                    collisionScores,
                    npcRuntimeUid,
                    defeated,
                    normalDropCardCode != 0 ? (byte)30 : (byte)0,
                    normalDropCardCode);
                QueueDungeonBroadcast(session, 0xD00E, collisionResponsePayload, false, "dungeon NPC collision");
                var loggedEffectiveAttack = (flags & 0x40) != 0
                    ? checked((int)(baseAttack * 1.5))
                    : baseAttack;
                _log($"{channel}:{remote} Dungeon projectile collision resolved: room={collisionRoom.Id} map={mapIndex} mode={mode} secondary={secondary} source={collisionSourceCode} targetIndex={targetIndex} flags=0x{flags:X2} runtime={npcRuntimeUid} resource={npcResourceUid} category={combatTemplate.AttackCategory} attackSource={(activeSkillCode == 0 ? "pet" : "skill")} skill={activeSkillCode} grade={activeSkillGrade} attack={baseAttack}->{loggedEffectiveAttack} defense={combatTemplate.Defense}->{effectiveDefense} damage={damage} hp={remainingHp}/{combatTemplate.Hp} defeated={defeated} scoreReward={(defeated ? combatTemplate.Score : 0)} cardDropBonus={cardDropBonusPercent}% sceneDrop={normalDropCardCode} targetLedger={npcTargetKey} scores={string.Join(',', collisionScores)}");
                var collisionFrame = BuildNativeFrame(frame, 0xD00E, collisionResponsePayload, session);
                if (!QuestMonsterCatalog.TryResolveTarget(
                        collisionRoom.BattleEpisode,
                        collisionRoom.BattleDungeon,
                        npcResourceUid,
                        out var questTargetCode))
                    return collisionFrame;

                try
                {
                    var progress = await _database.AdvanceQuestMonsterHitAsync(
                        session.AccountId,
                        session.Character.Id,
                        session.SessionId,
                        questTargetCode,
                        token);
                    if (!progress.Authorized || !progress.Changed)
                        return collisionFrame;

                    _log($"{channel}:{remote} Quest monster-hit progress: character={session.Character.Id} target={questTargetCode} episode={collisionRoom.BattleEpisode} dungeon={collisionRoom.BattleDungeon} runtime={npcRuntimeUid} resource={npcResourceUid} completed={progress.NewlyCompleted}");
                    var taskListFrame = BuildNativeFrame(
                        frame, 0xC59C, BuildTaskListPayload(progress.Tasks), session);
                    if (!progress.NewlyCompleted)
                        return CombineNativeFrames(collisionFrame, taskListFrame);

                    // Retail C59D consumes no payload fields; it only opens the
                    // completed-task notification UI.
                    var completedFrame = BuildNativeFrame(frame, 0xC59D, [], session);
                    return CombineNativeFrames(collisionFrame, taskListFrame, completedFrame);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log($"{channel}:{remote} Quest monster-hit persistence failed without changing dungeon response: target={questTargetCode} error={ex.Message}");
                    return collisionFrame;
                }
            }

            case 0xD011: // REQ_FLYSHOOTING_BOSS_RECORDING -> ANS_FLYSHOOTING_BOSS_RECORDING
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != DungeonBossRecordingPayloadLength)
                    return null;
                var bossRoom = GetDungeonRoom(session);
                var bossBattle = bossRoom?.Battle;
                var mode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var secondary = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var attackSourceCode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
                var projectileIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                var bossObjectIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2));
                var parentIndex = payload[0x0A];
                var childIndex = payload[0x0B];
                var componentIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0x0C, 4));
                IReadOnlyList<KeyValuePair<ushort, DungeonBossTemplate>> activeBosses = [];
                if (bossRoom is null
                    || bossBattle is null
                    || !bossRoom.Started
                    || componentIndex < 0
                    || bossObjectIndex > byte.MaxValue
                    || bossRoom.SelectedMapIndex is not { } mapIndex
                    || (activeBosses = DungeonCombatCatalog.GetInitiallyScheduledBosses(
                            bossRoom.HdIndex,
                            bossRoom.BattleEpisode,
                            bossRoom.BattleDungeon,
                            bossRoom.BattleStage,
                            mapIndex)).Count <= bossObjectIndex
                    || !activeBosses[(int)bossObjectIndex].Value.Components.TryGetValue(
                        new DungeonBossComponentKey(parentIndex, childIndex, componentIndex),
                        out var componentTemplate))
                {
                    _log($"{channel}:{remote} Rejected dungeon BOSS identity/resource: room={bossRoom?.Id ?? 0} map={bossRoom?.SelectedMapIndex?.ToString() ?? "none"} object={bossObjectIndex} source={attackSourceCode} projectile={projectileIndex} component={parentIndex}/{childIndex}/{componentIndex}");
                    return null;
                }
                var activeBoss = activeBosses[(int)bossObjectIndex];
                var bossResourceUid = activeBoss.Key;
                var bossTemplate = activeBoss.Value;

                // Retail sub_670E10 serializes sub_41E4C5(0..4) at D011
                // +0x10..+0x14. State 1 multiplies attack by 1.5, while
                // state 0 halves the target component's defense.
                var defenseBreakActive = payload[0x10] != 0;
                var attackBoostActive = payload[0x11] != 0;
                var petState = PetProgression.GetState(
                    session.Character,
                    GetEquippedPetItemCode(session.Character));
                var attackProfile = PetProgression.GetAttackProfile(petState);
                var normalBaseAttack = attackProfile.GetForCategory(componentTemplate.AttackCategory);
                var effectiveDefense = defenseBreakActive
                    ? checked((int)(componentTemplate.Defense * 0.5))
                    : componentTemplate.Defense;

                uint bossEnergy;
                bool clearedNow;
                int baseAttack;
                int effectiveAttack;
                uint activeSkillCode;
                byte activeSkillGrade;
                int damage;
                int componentEnergy;
                bool componentResolvedNow;
                bool hiddenSuperBossTailResolved = false;
                uint bossDropCardCode = 0;
                uint[] bossScores;
                Dictionary<long, int> bossHansRewards = [];
                var clearRanks = new byte[4];
                lock (_dungeonRoomGate)
                {
                    if (!ReferenceEquals(bossRoom.Battle, bossBattle)
                        || bossBattle.State != DungeonBattleState.Active
                        || !bossRoom.Members.ContainsKey(session.SessionId)
                        || bossBattle.DeadCharacters.Contains(session.Character.Id))
                    {
                        _log($"{channel}:{remote} Rejected dungeon BOSS hit after battle-state validation: room={bossRoom.Id} character={session.Character.Id} sameInstance={ReferenceEquals(bossRoom.Battle, bossBattle)} state={bossBattle.State} dead={bossBattle.DeadCharacters.Contains(session.Character.Id)}");
                        return null;
                    }

                    baseAttack = GetDungeonBaseAttackLocked(
                        bossBattle,
                        session.Character.Id,
                        normalBaseAttack,
                        Environment.TickCount64,
                        out activeSkillCode,
                        out activeSkillGrade);
                    effectiveAttack = attackBoostActive
                        ? checked((int)(baseAttack * 1.5))
                        : baseAttack;

                    if (!bossRoom.Bosses.TryGetValue(bossResourceUid, out var bossState))
                    {
                        bossState = new DungeonBossState(bossTemplate);
                        bossRoom.Bosses.Add(bossResourceUid, bossState);
                    }

                    var componentKey = new DungeonBossComponentKey(
                        parentIndex,
                        childIndex,
                        componentIndex);
                    if (!ReferenceEquals(bossState.Template, bossTemplate)
                        || !bossState.Components.TryGetValue(componentKey, out var componentState)
                        || componentState.Template != componentTemplate)
                    {
                        _log($"{channel}:{remote} Rejected dungeon BOSS template mutation: room={bossRoom.Id} map={mapIndex} uid={bossResourceUid} component={parentIndex}/{childIndex}/{componentIndex}");
                        return null;
                    }

                    damage = componentState.Defeated
                        ? 0
                        : Math.Min(
                            effectiveAttack > effectiveDefense
                                ? effectiveAttack - effectiveDefense
                                : 1,
                            componentState.CurrentHp);
                    componentState.CurrentHp -= damage;
                    componentEnergy = componentState.CurrentHp;
                    var hiddenComponentKey = new DungeonBossComponentKey(1, 0, 8);
                    if (componentState.Defeated
                        && !componentState.ResolutionAnnounced
                        && bossRoom.HdIndex == 0
                        && bossRoom.BattleEpisode == 0
                        && bossRoom.BattleDungeon == 2
                        && bossRoom.BattleStage == 1
                        && bossTemplate.TotalHp == 25_000
                        && bossTemplate.TotalScore == 11_000
                        && bossTemplate.Components.Count == 2
                        && componentKey == new DungeonBossComponentKey(0, 0, 9)
                        && componentTemplate.Hp == 15_000
                        && bossTemplate.Components.TryGetValue(hiddenComponentKey, out var hiddenComponentTemplate)
                        && hiddenComponentTemplate.Hp == 10_000
                        && bossState.Components.TryGetValue(hiddenComponentKey, out var hiddenComponentState)
                        && hiddenComponentState.CurrentHp > 0)
                    {
                        // bbm_03 exposes only p0/b0/c9 to retail attacks. Its p1/b0/c8
                        // tail never becomes targetable, so resolve that resource-only
                        // HP when the visible component dies and let D012 clear the BOSS.
                        hiddenComponentState.CurrentHp = 0;
                        hiddenComponentState.ResolutionAnnounced = true;
                        hiddenSuperBossTailResolved = true;
                    }
                    bossEnergy = checked((uint)bossState.CurrentEnergy);
                    bossRoom.Battle.LastBossResourceUid = bossResourceUid;
                    componentResolvedNow = componentState.Defeated && !componentState.ResolutionAnnounced;
                    if (componentResolvedNow)
                        componentState.ResolutionAnnounced = true;
                    clearedNow = bossState.Defeated && !bossState.ClearAnnounced;
                    if (clearedNow)
                    {
                        bossState.ClearAnnounced = true;
                        // D012 has no scorer UID. The retail client applies its
                        // BOSS score to every recipient's local total, so the
                        // battle snapshot must award the same bonus to every
                        // participant instead of folding it into one member's
                        // ordinary HitScore field.
                        foreach (var participantId in bossRoom.Battle.ParticipantCharacterIds)
                        {
                            bossRoom.BossBonusScores[participantId] = checked(
                                bossRoom.BossBonusScores.GetValueOrDefault(participantId)
                                + bossTemplate.TotalScore);
                        }
                        if (DungeonCombatCatalog.TryGetMaximumScore(
                                bossRoom.HdIndex,
                                bossRoom.BattleEpisode,
                                bossRoom.BattleDungeon,
                                bossRoom.BattleStage,
                                mapIndex,
                                out var maximumScore))
                        {
                            foreach (var member in bossRoom.Members.Values)
                            {
                                var participant = member.Session.Character;
                                if (participant is null
                                    || !bossRoom.Battle.ParticipantCharacterIds.Contains(participant.Id))
                                    continue;
                                var participantScore = checked(
                                    bossRoom.HitScores.GetValueOrDefault(participant.Id)
                                    + bossRoom.BossBonusScores.GetValueOrDefault(participant.Id));
                                var rating = DungeonRewardPolicy.CalculateRatingFromScore(
                                    participantScore,
                                    maximumScore.TotalScore,
                                    cleared: true);
                                if (member.SlotIndex < clearRanks.Length)
                                    clearRanks[member.SlotIndex] = rating;
                                var reward = DungeonRewardPolicy.Calculate(
                                    rating,
                                    bossRoom.Battle.PartySizeAtStart,
                                    cleared: true,
                                    PetProgression.GetHansBonusPercent(PetProgression.GetState(
                                        participant,
                                        GetEquippedPetItemCode(participant))));
                                bossRoom.BossHansRewards[participant.Id] = reward.Hans;
                                bossHansRewards[participant.Id] = reward.Hans;
                            }
                        }
                    }
                    if (clearedNow && CardCatalog.TrySelectDungeonBossDrop(
                            bossRoom.BattleEpisode,
                            bossRoom.BattleDungeon,
                            componentTemplate.ResourceCode,
                            out var bossCard))
                    {
                        RegisterGeneratedDungeonCardLocked(bossRoom, bossCard.CardCode);
                        bossDropCardCode = bossCard.CardCode;
                    }
                    bossScores = BuildDungeonCollisionScoresLocked(bossRoom);
                }

                // Retail D012 is a component-resolution notification. Its non-zero
                // branch calls sub_6CD770 and monotonically changes the selected BMO
                // component to state 2, so sending it for an ordinary hit removes the
                // component before its official HP is exhausted. The client already
                // applies each local hit to the BOSS energy bar; only acknowledge the
                // hit that actually resolves this component.
                if (!componentResolvedNow)
                {
                    _log($"{channel}:{remote} Dungeon BOSS damage accumulated: room={bossRoom.Id} map={mapIndex} mode={mode} secondary={secondary} object={bossObjectIndex} uid={bossResourceUid} source={attackSourceCode} projectile={projectileIndex} component={parentIndex}/{childIndex}/{componentIndex} category={componentTemplate.AttackCategory} attackSource={(activeSkillCode == 0 ? "pet" : "skill")} skill={activeSkillCode} grade={activeSkillGrade} attack={baseAttack}->{effectiveAttack} defense={componentTemplate.Defense}->{effectiveDefense} damage={damage} componentHp={componentEnergy}/{componentTemplate.Hp} bossEnergy={bossEnergy}/{bossTemplate.TotalHp} resolved=False");
                    return null;
                }

                var stageStates = Enumerable.Repeat((byte)11, 4).ToArray();
                if (bossEnergy == 0)
                {
                    foreach (var member in bossRoom.Members.Values)
                    {
                        if (member.SlotIndex < stageStates.Length)
                            stageStates[member.SlotIndex] = 20;
                    }
                }
                byte[] BuildBossResponsePayload(long characterId) => DungeonProtocol.BuildBoss(
                    bossScores,
                    bossEnergy,
                    stageStates,
                    clearRanks,
                    new ushort[4],
                    new ushort[4],
                    checked((byte)bossObjectIndex),
                    parentIndex,
                    checked((ushort)componentIndex),
                    clearedNow
                        ? checked((uint)bossHansRewards.GetValueOrDefault(characterId))
                        : 0,
                    bossDropCardCode);

                var bossResponsePayload = BuildBossResponsePayload(session.Character.Id);
                lock (_dungeonRoomGate)
                {
                    if (ReferenceEquals(bossRoom.Battle, bossBattle))
                    {
                        foreach (var member in bossRoom.Members.Values)
                        {
                            if (member.Session.SessionId == session.SessionId
                                || member.Session.Character is not { } recipient
                                || !_activeWorldSessions.TryGetValue(member.Session.SessionId, out var target))
                                continue;
                            session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                                target,
                                0xD012,
                                BuildBossResponsePayload(recipient.Id),
                                "dungeon boss state"));
                        }
                    }
                }
                var immediateHans = bossHansRewards.GetValueOrDefault(session.Character.Id);
                _log($"{channel}:{remote} Dungeon BOSS component resolved: room={bossRoom.Id} map={mapIndex} mode={mode} secondary={secondary} object={bossObjectIndex} uid={bossResourceUid} source={attackSourceCode} projectile={projectileIndex} component={parentIndex}/{childIndex}/{componentIndex} category={componentTemplate.AttackCategory} attackSource={(activeSkillCode == 0 ? "pet" : "skill")} skill={activeSkillCode} grade={activeSkillGrade} attack={baseAttack}->{effectiveAttack} defense={componentTemplate.Defense}->{effectiveDefense} damage={damage} componentHp={componentEnergy}/{componentTemplate.Hp} bossEnergy={bossEnergy}/{bossTemplate.TotalHp} hiddenTailResolved={hiddenSuperBossTailResolved} cleared={clearedNow} bossBonusScore={(clearedNow ? bossTemplate.TotalScore : 0)} immediateHans={immediateHans} sceneDrop={bossDropCardCode}");
                return BuildNativeFrame(frame, 0xD012, bossResponsePayload, session);
            }

            case 0xD034: // REQ_FLYSHOOTING_OBTAIN_ITEM -> ANS_FLYSHOOTING_OBTAIN_ITEM
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != DungeonPickupPayloadLength)
                    return null;
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                        return null;
                    var arenaPickupType = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                    var arenaDropUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                    var arenaPickupValue = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                    var arenaPickupRoom = GetArenaRoom(session);
                    bool firstClaim;
                    lock (_arenaRoomGate)
                    {
                        if (arenaPickupRoom is null
                            || !arenaPickupRoom.Started
                            || !arenaPickupRoom.Members.ContainsKey(session.SessionId))
                            return null;
                        firstClaim = arenaPickupRoom.ClaimedDrops.Add((arenaPickupType, arenaDropUid));
                    }
                    var arenaPickupPayload = BuildDungeonPickupPayload(
                        session.Character,
                        FindArenaRoomOwner(session)?.Character,
                        arenaPickupType,
                        arenaDropUid,
                        arenaPickupValue);
                    QueueArenaBroadcast(
                        session,
                        0xD035,
                        arenaPickupPayload,
                        false,
                        "arena item removal");
                    _log($"{channel}:{remote} Arena item removed: room={arenaPickupRoom.Id} type={arenaPickupType} uid={arenaDropUid} value={arenaPickupValue} first={firstClaim}");
                    return BuildNativeFrame(frame, 0xD035, arenaPickupPayload, session);
                }
                var pickupRoom = GetDungeonRoom(session);
                var pickupBattle = pickupRoom?.Battle;
                if (pickupRoom is null || pickupBattle is null || !pickupRoom.Started)
                    return null;
                var pickupType = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var dropUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var pickupValue = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                byte expectedNormalDropType = 0;
                bool accepted;
                ushort reservedGeneratedToken = 0;
                uint cardToPersist = 0;
                byte persistedCardQuantity = 0;
                int pickupScore;
                var hpBeforeRecovery = session.Character.CurrentHp;
                var mpBeforeRecovery = session.Character.CurrentMp;
                var recoveryApplied = false;
                lock (_dungeonRoomGate)
                {
                    if (!ReferenceEquals(pickupRoom.Battle, pickupBattle)
                        || pickupBattle.State != DungeonBattleState.Active
                        || !pickupRoom.Members.ContainsKey(session.SessionId)
                        || pickupBattle.DeadCharacters.Contains(session.Character.Id))
                    {
                        _log($"{channel}:{remote} Rejected dungeon pickup after battle-state validation: room={pickupRoom.Id} character={session.Character.Id} sameInstance={ReferenceEquals(pickupRoom.Battle, pickupBattle)} state={pickupBattle.State} dead={pickupBattle.DeadCharacters.Contains(session.Character.Id)}");
                        return null;
                    }

                    if (dropUid < 50
                        && pickupBattle.UpgradeDropSubtypes is { } upgradeDropPlan
                        && DungeonProtocol.TryGetSpawnedUpgradeDropSubtype(
                            upgradeDropPlan, dropUid, out var spawnedDropType))
                        expectedNormalDropType = spawnedDropType;

                    // Retail card scene objects can serialize D034 as type 20
                    // (the D00E/MMO path) or type 30 (the BOSS/BMO path). Type
                    // 20 is also used by CFEC numeric-item entities, so a
                    // adapter-generated CardCode must win that overlap before
                    // falling back to the per-UID numeric-item table.
                    var generatedCard = pickupType is 20 or 30
                        ? pickupBattle.GeneratedDrops.FirstOrDefault(drop =>
                            drop.Value == pickupValue)
                        : default;
                    var isGeneratedCard = pickupType is 20 or 30
                        && generatedCard.Key != 0
                        && generatedCard.Value == pickupValue
                        && CardCatalog.TryGet(pickupValue, out _);
                    uint expectedInDungeonItemValue = 0;
                    var hasExpectedInDungeonItemValue = pickupBattle.InDungeonItemDropCodes is { } itemDropPlan
                        && DungeonProtocol.TryGetInDungeonItemDropValue(
                            itemDropPlan, dropUid, out expectedInDungeonItemValue);
                    var validReward = pickupType switch
                    {
                        20 => isGeneratedCard
                              || (hasExpectedInDungeonItemValue
                                  && pickupValue == expectedInDungeonItemValue),
                        // CFEC type-40 entities are the retail in-battle skill/pet
                        // upgrade boxes. They are not card-book rewards.
                        40 => expectedNormalDropType != 0 && pickupValue == expectedNormalDropType,
                        30 => isGeneratedCard,
                        _ => false
                    };
                    if (!validReward)
                    {
                        accepted = false;
                    }
                    else if (!isGeneratedCard)
                    {
                        accepted = pickupBattle.ClaimedDrops.Add((pickupType, dropUid));
                        if (accepted && pickupType == 40 && pickupValue is 2 or 3)
                        {
                            ApplyDungeonUpgradePickupRecovery(
                                session.Character,
                                checked((byte)pickupValue),
                                pickupBattle.PartySizeAtStart);
                            recoveryApplied = session.Character.CurrentHp != hpBeforeRecovery
                                || session.Character.CurrentMp != mpBeforeRecovery;
                        }
                    }
                    else
                    {
                        accepted = generatedCard.Value == pickupValue
                            && pickupBattle.ClaimedDrops.Add((pickupType, dropUid));
                        if (accepted)
                        {
                            pickupBattle.GeneratedDrops.Remove(generatedCard.Key);
                            reservedGeneratedToken = generatedCard.Key;
                            cardToPersist = pickupValue;
                        }
                    }

                    pickupScore = pickupBattle.HitScores.GetValueOrDefault(session.Character.Id);
                }

                var recoveryPersisted = !recoveryApplied;
                if (accepted && recoveryApplied)
                {
                    recoveryPersisted = await _database.SaveCharacterRuntimeStateAsync(
                        session.AccountId,
                        session.Character.Id,
                        session.SessionId,
                        CreateRuntimeState(session.Character, session.ChannelId),
                        token);
                }

                if (accepted && cardToPersist != 0)
                {
                    (bool Success, byte Quantity) cardGrant;
                    try
                    {
                        cardGrant = await _database.GrantDungeonCardAsync(
                            session.AccountId,
                            session.Character.Id,
                            session.SessionId,
                            cardToPersist,
                            token);
                    }
                    catch
                    {
                        lock (_dungeonRoomGate)
                        {
                            if (ReferenceEquals(pickupRoom.Battle, pickupBattle))
                            {
                                pickupBattle.ClaimedDrops.Remove((pickupType, dropUid));
                                pickupBattle.GeneratedDrops.TryAdd(
                                    reservedGeneratedToken,
                                    cardToPersist);
                            }
                        }
                        throw;
                    }

                    if (!cardGrant.Success)
                    {
                        lock (_dungeonRoomGate)
                        {
                            if (ReferenceEquals(pickupRoom.Battle, pickupBattle))
                            {
                                pickupBattle.ClaimedDrops.Remove((pickupType, dropUid));
                                pickupBattle.GeneratedDrops.TryAdd(
                                    reservedGeneratedToken,
                                    cardToPersist);
                            }
                        }
                        accepted = false;
                    }
                    else
                    {
                        persistedCardQuantity = cardGrant.Quantity;
                        AccountStateChanged?.Invoke();
                    }
                }

                var pickupResponsePayload = BuildDungeonPickupPayload(
                    session.Character, FindDungeonRoomOwner(session)?.Character,
                    pickupType, dropUid, accepted ? pickupValue : 0);
                QueueDungeonBroadcast(session, 0xD035, pickupResponsePayload, false, "dungeon pickup");
                _log($"{channel}:{remote} Dungeon pickup: room={pickupRoom.Id} type={pickupType} uid={dropUid} value={pickupValue} expectedCardType={expectedNormalDropType} accepted={accepted} persistedCardQuantity={persistedCardQuantity} recoveryApplied={recoveryApplied} recoveryPersisted={recoveryPersisted} hp={session.Character.CurrentHp}/{session.Character.MaxHp} mp={session.Character.CurrentMp}/{session.Character.MaxMp} score={pickupScore}");
                return BuildNativeFrame(frame, 0xD035, pickupResponsePayload, session);
            }

            case 0xCF87: // REQ_FLYSHOOTING_END_GAME_INFO -> ANS_FLYSHOOTING_END_GAME_INFO
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != DungeonEndGamePayloadLength)
                    return null;
                var endRoom = GetDungeonRoom(session);
                if (endRoom is null)
                    return null;
                DungeonBattleInstance endBattle;
                lock (_dungeonRoomGate)
                {
                    endBattle = endRoom.Battle;
                    if (endBattle.State != DungeonBattleState.Active
                        || !endRoom.Members.ContainsKey(session.SessionId))
                    {
                        _log($"{channel}:{remote} Rejected dungeon reward outside its active instance: room={endRoom.Id} character={session.Character.Id} state={endBattle.State}");
                        return null;
                    }
                    endBattle.SettlementStarted = true;
                }
                // Retail CF87 publishes the local entity's current MP first,
                // followed by elapsed whole minutes. Skill costs remain
                // adapter-owned; accept only bounded upward recovery that the
                // client applied locally during this battle.
                var reportedCurrentMp = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var elapsedMinutes = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                int hitScore;
                int bossBonusScore;
                int score;
                int stageRecordScore;
                DungeonMaximumScore maximumScore;
                byte[]? cachedEndPayload;
                var rewardCharacterId = session.Character.Id;
                var rewardEpisode = endBattle.Episode;
                var rewardDungeon = endBattle.Dungeon;
                var rewardStage = endBattle.Stage;
                var rewardDifficulty = endRoom.BattleLogicalDifficulty;
                var rewardWireDifficulty = endRoom.Difficulty;
                bool rewardCleared;
                int rewardPartySize;
                lock (_dungeonRoomGate)
                {
                    if (!ReferenceEquals(endRoom.Battle, endBattle)
                        || endBattle.State != DungeonBattleState.Active
                        || !endRoom.Members.ContainsKey(session.SessionId))
                    {
                        _log($"{channel}:{remote} Rejected stale dungeon reward request: room={endRoom.Id} character={rewardCharacterId} sameInstance={ReferenceEquals(endRoom.Battle, endBattle)} state={endBattle.State}");
                        return null;
                    }
                    if (endBattle.EndGamePayloads.TryGetValue(
                            rewardCharacterId,
                            out cachedEndPayload))
                    {
                        _log($"{channel}:{remote} Dungeon reward response replayed: room={endRoom.Id} character={rewardCharacterId}");
                        return BuildNativeFrame(frame, 0xCF88, cachedEndPayload, session);
                    }
                    if (endBattle.RewardCommittingCharacters.Contains(rewardCharacterId)
                        || endBattle.RewardedCharacters.Contains(rewardCharacterId))
                    {
                        _log($"{channel}:{remote} Dungeon reward response deferred while commit is in progress: room={endRoom.Id} character={rewardCharacterId}");
                        return null;
                    }
                    hitScore = endBattle.HitScores.GetValueOrDefault(rewardCharacterId);
                    bossBonusScore = endBattle.BossBonusScores.GetValueOrDefault(rewardCharacterId);
                    score = checked(hitScore + bossBonusScore);
                    // CF16 associates one team stage-record score with every
                    // participating name. Ordinary scores are per scorer, while
                    // the BOSS bonus is mirrored to each member, so add it once.
                    stageRecordScore = checked(
                        endBattle.ParticipantCharacterIds.Sum(characterId =>
                            endBattle.HitScores.GetValueOrDefault(characterId))
                        + endBattle.ParticipantCharacterIds
                            .Select(characterId => endBattle.BossBonusScores.GetValueOrDefault(characterId))
                            .DefaultIfEmpty(0)
                            .Max());
                    rewardCleared = endBattle.Bosses.Values.Any(boss => boss.ClearAnnounced);
                    // Settlement belongs to the battle instance, not to the
                    // mutable waiting-room membership. A member leaving after
                    // the BOSS dies must not change the party multiplier for
                    // the players who are still committing this instance.
                    rewardPartySize = endBattle.PartySizeAtStart;
                    if (rewardPartySize is < 1 or > 3
                        || !endBattle.ParticipantCharacterIds.Contains(rewardCharacterId))
                    {
                        _log($"{channel}:{remote} Rejected dungeon reward without a valid battle participant snapshot: room={endRoom.Id} character={rewardCharacterId} party={rewardPartySize}");
                        return null;
                    }
                    if (endBattle.SelectedMapIndex is not ushort rewardMapIndex
                        || !DungeonCombatCatalog.TryGetMaximumScore(
                            endBattle.HdIndex,
                            rewardEpisode,
                            rewardDungeon,
                            rewardStage,
                            rewardMapIndex,
                            out maximumScore))
                    {
                        _log($"{channel}:{remote} Rejected dungeon reward without an official score maximum: room={endRoom.Id} character={rewardCharacterId} selectors={endBattle.HdIndex}/{rewardEpisode}/{rewardDungeon}/{rewardStage} map={endBattle.SelectedMapIndex}");
                        return null;
                    }
                    endBattle.RewardCommittingCharacters.Add(rewardCharacterId);
                }
                try
                {
                    var mpBeforeReconciliation = session.Character.CurrentMp;
                    var mpReconciled = TryApplyDungeonReportedMp(
                        session.Character,
                        reportedCurrentMp);
                    if (mpReconciled
                        && !await _database.SaveCharacterRuntimeStateAsync(
                            session.AccountId,
                            session.Character.Id,
                            session.SessionId,
                            CreateRuntimeState(session.Character, session.ChannelId),
                            token))
                    {
                        session.Character.CurrentMp = mpBeforeReconciliation;
                        return null;
                    }
                    var levelBeforeReward = session.Character.Level;
                    var experienceBeforeReward = session.Character.Experience;
                    var petBeforeReward = PetProgression.GetState(
                        session.Character,
                        GetEquippedPetItemCode(session.Character));
                    var rewardRating = DungeonRewardPolicy.CalculateRatingFromScore(
                        score,
                        maximumScore.TotalScore,
                        rewardCleared);
                    var settlementReward = DungeonRewardPolicy.Calculate(
                        rewardRating,
                        rewardPartySize,
                        rewardCleared,
                        PetProgression.GetHansBonusPercent(petBeforeReward));
                    // Retail stores Dungeon 1/2/3/Super-BOSS in bits 0/1/2/3
                    // of the same [episode][difficulty] C355 entry.
                    var rewardProgressCompleted = rewardCleared;
                    var rewardIsSuperBoss = IsDungeonSuperBoss(rewardDungeon, rewardStage);
                    var progressBefore = await _database.GetDungeonClearMasksAsync(
                        rewardCharacterId,
                        token);
                    var rewarded = await _database.ApplyDungeonRewardAsync(
                        session.AccountId,
                        rewardCharacterId,
                        session.SessionId,
                        endBattle.HdIndex,
                        rewardEpisode,
                        rewardDungeon,
                        rewardDifficulty,
                        score,
                        elapsedMinutes,
                        settlementReward.CharacterExperience,
                        settlementReward.PetExperience,
                        settlementReward.Hans,
                        token,
                        completed: rewardProgressCompleted,
                        superBoss: rewardIsSuperBoss,
                        clearRating: settlementReward.Rating,
                        stageRecordScore: stageRecordScore);
                    if (rewarded is null)
                        return null;

                    session.Character = rewarded;
                    var petAfterReward = PetProgression.GetState(
                        session.Character,
                        GetEquippedPetItemCode(session.Character));
                    var petLevelUp = petAfterReward.Level != petBeforeReward.Level
                        || petAfterReward.CurrentStage != petBeforeReward.CurrentStage;
                    var progressAfter = await _database.GetDungeonClearMasksAsync(
                        rewardCharacterId,
                        token);
                    var progressIndex = rewardEpisode * DungeonDifficultyCount + rewardDifficulty;
                    var archiveBit = 1 << (rewardDungeon + rewardStage);
                    var progressDisposition = rewardProgressCompleted
                        ? rewardIsSuperBoss
                            ? "committed to Super-BOSS archive"
                            : "committed to dungeon archive"
                        : rewardCleared
                            ? "preserved after non-archived clear"
                            : "preserved after failure";
                    _log($"{channel}:{remote} Dungeon progress {progressDisposition}: character={rewardCharacterId} episode={rewardEpisode} dungeon={rewardDungeon} realStage={rewardStage} wireDifficulty={rewardWireDifficulty} logicalDifficulty={rewardDifficulty} maskIndex={progressIndex} archiveBit=0x{archiveBit:X2} before=0x{progressBefore[progressIndex]:X2} after=0x{progressAfter[progressIndex]:X2}");
                    AccountStateChanged?.Invoke();

                    var ownerCharacter = FindDungeonRoomOwner(session)?.Character ?? session.Character;
                    var levelStart = CharacterProgression.ExperienceRequiredForLevel(session.Character.Level);
                    var nextLevel = session.Character.Level >= CharacterProgression.MaximumLevel
                        ? levelStart + 1
                        : CharacterProgression.ExperienceRequiredForLevel(session.Character.Level + 1);
                    var actualGainedExperience = (uint)Math.Clamp(
                        session.Character.Experience - experienceBeforeReward,
                        0L,
                        uint.MaxValue);
                    var endPayload = BuildDungeonEndGamePayload(
                        ownerCharacter,
                        session.Character,
                        actualGainedExperience,
                        levelStart,
                        nextLevel,
                        hitScore,
                        settlementReward.Rating,
                        petLevelUp ? 1 : 0,
                        bonusScore: bossBonusScore,
                        earnedHans: settlementReward.Hans,
                        playerLevelUpState: session.Character.Level != levelBeforeReward ? 1 : 0);
                    lock (_dungeonRoomGate)
                    {
                        endBattle.RewardedCharacters.Add(rewardCharacterId);
                        endBattle.EndGamePayloads[rewardCharacterId] = endPayload;
                    }
                    var clientExperience = BinaryPrimitives.ReadUInt32LittleEndian(
                        endPayload.AsSpan(0x14, 4));
                    var clientLevelStart = BinaryPrimitives.ReadUInt32LittleEndian(
                        endPayload.AsSpan(0x18, 4));
                    var clientNextLevel = BinaryPrimitives.ReadUInt32LittleEndian(
                        endPayload.AsSpan(0x1C, 4));
                    var equippedPetState = PetProgression.GetState(session.Character, GetEquippedPetItemCode(session.Character));
                    _log($"{channel}:{remote} Dungeon reward persisted: room={endRoom.Id} character={rewardCharacterId} cleared={rewardCleared} rating={settlementReward.Rating} party={rewardPartySize} hitScore={hitScore}/{maximumScore.HitScore} bossBonus={bossBonusScore}/{maximumScore.BossBonusScore} totalScore={score}/{maximumScore.TotalScore} reportedMp={reportedCurrentMp} authoritativeMp={session.Character.CurrentMp} reward=exp:{settlementReward.CharacterExperience}/pet:{settlementReward.PetExperience}/hans:{settlementReward.Hans} level={session.Character.Level} exp={session.Character.Experience} clientExp={clientExperience} levelRange={clientLevelStart}-{clientNextLevel} hans={session.Character.Hans} pet={equippedPetState.ItemCode} petStage={equippedPetState.CurrentStage}/{equippedPetState.MaximumStage} petLevel={equippedPetState.Level} petExp={equippedPetState.Experience} petLevelUp={petLevelUp}");
                    return BuildNativeFrame(frame, 0xCF88, endPayload, session);
                }
                finally
                {
                    lock (_dungeonRoomGate)
                        endBattle.RewardCommittingCharacters.Remove(rewardCharacterId);
                }
            }

            case 0xCF8B: // REQ_FLYSHOOTING_RESETTING -> ANS_FLYSHOOTING_RESETTING
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != DungeonResettingPayloadLength)
                    return null;
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (!session.AuxiliaryGameSession
                        || !TryResetArenaRound(session, payload, out var arenaRoomId, out var arenaResetPayload, out var roundReset))
                        return null;
                    if (roundReset)
                        QueueArenaLobbyRoomListRefresh(session, "arena round reset completed");
                    _log($"{channel}:{remote} Arena round reset acknowledged: room={arenaRoomId} character={session.Character.Id} realStage={payload[0]} showStage={payload[1]} mode={BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2))} completed={roundReset}");
                    return BuildNativeFrame(frame, 0xCF8C, arenaResetPayload, session);
                }
                var resetRoom = GetDungeonRoom(session);
                if (resetRoom is null || resetRoom.OwnerSessionId != session.SessionId)
                    return null;

                var requestedRealStage = payload[0];
                var requestedShowStage = payload[1];
                var resetMode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                if (resetMode is not (1 or 2))
                    return null;
                byte sourceEpisode;
                byte sourceDungeon;
                byte sourceStage;
                byte sourceWireDifficulty;
                byte sourceLogicalDifficulty;
                ushort sourceRealStage;
                bool sourceBossCleared;
                lock (_dungeonRoomGate)
                {
                    sourceEpisode = resetRoom.BattleEpisode;
                    sourceDungeon = resetRoom.BattleDungeon;
                    sourceStage = resetRoom.BattleStage;
                    sourceWireDifficulty = resetRoom.Difficulty;
                    sourceLogicalDifficulty = resetRoom.BattleLogicalDifficulty;
                    sourceRealStage = resetRoom.Battle.RequestedStageIndex;
                    sourceBossCleared = resetRoom.BossClearAnnounced;
                    if (!resetRoom.Battle.SettlementStarted
                        || resetRoom.Battle.ContinuingCharacters.Count != 0
                        || resetRoom.Battle.RewardCommittingCharacters.Count != 0
                        || HasPendingDungeonRewardsLocked(resetRoom))
                    {
                        _log($"{channel}:{remote} Rejected dungeon result action before settlement completion: room={resetRoom.Id} settlementStarted={resetRoom.Battle.SettlementStarted} continues={resetRoom.Battle.ContinuingCharacters.Count} committingRewards={resetRoom.Battle.RewardCommittingCharacters.Count} pendingRewards={HasPendingDungeonRewardsLocked(resetRoom)}");
                        return null;
                    }
                }
                if (sourceRealStage != sourceStage)
                {
                    _log($"{channel}:{remote} Rejected dungeon result action from a mismatched active stage: room={resetRoom.Id} requested={requestedRealStage}/{requestedShowStage} sourceStage={sourceStage} sourceReal={sourceRealStage}");
                    return null;
                }

                DungeonSettlementAction resetAction;
                byte targetDungeonSelection;
                byte targetStageSelection;
                byte targetLogicalDifficulty;
                // The retail result page uses mode 1 for both Retry and the
                // final-dungeon Challenge Super-BOSS button. Challenge is
                // identified by RealStage 1; ShowStage is client-owned and is
                // observed as both 0 and 2 on the original result paths.
                if (resetMode == 1
                    && sourceBossCleared
                    && sourceDungeon == DungeonCountPerEpisode - 1
                    && sourceStage == 0
                    && requestedRealStage == 1)
                {
                    resetAction = DungeonSettlementAction.ChallengeBoss;
                    targetDungeonSelection = sourceDungeon;
                    targetStageSelection = 1;
                    targetLogicalDifficulty = sourceLogicalDifficulty;
                }
                else if (resetMode == 1
                         && (requestedRealStage == sourceStage
                             || (IsDungeonSuperBoss(sourceDungeon, sourceStage)
                                 && requestedRealStage == 0)))
                {
                    resetAction = DungeonSettlementAction.RetryCurrent;
                    targetDungeonSelection = sourceDungeon;
                    targetStageSelection = sourceStage;
                    targetLogicalDifficulty = sourceLogicalDifficulty;
                }
                else if (resetMode == 2
                         && sourceBossCleared
                         && sourceDungeon < DungeonCountPerEpisode - 1
                         && sourceStage == 0
                         && requestedRealStage == 0)
                {
                    resetAction = DungeonSettlementAction.NextDungeon;
                    targetDungeonSelection = checked((byte)(sourceDungeon + 1));
                    targetStageSelection = 0;
                    targetLogicalDifficulty = sourceLogicalDifficulty;
                }
                else if (resetMode == 2
                         && sourceBossCleared
                         && IsDungeonSuperBoss(sourceDungeon, sourceStage)
                         && requestedRealStage == 0
                         && sourceLogicalDifficulty < DungeonDifficultyCount - 1)
                {
                    resetAction = DungeonSettlementAction.NextDifficulty;
                    targetDungeonSelection = 0;
                    targetStageSelection = 0;
                    targetLogicalDifficulty = checked((byte)(sourceLogicalDifficulty + 1));
                }
                else
                {
                    _log($"{channel}:{remote} Rejected ambiguous dungeon result action: room={resetRoom.Id} mode={resetMode} sourceDungeon={sourceDungeon} sourceStage={sourceStage} requested={requestedRealStage}/{requestedShowStage} bossCleared={sourceBossCleared} wireDifficulty={sourceWireDifficulty} logicalDifficulty={sourceLogicalDifficulty}");
                    return null;
                }

                if ((resetAction is DungeonSettlementAction.NextDungeon
                        or DungeonSettlementAction.NextDifficulty)
                    && !await CanDungeonRoomMembersEnterSelectionAsync(
                        resetRoom,
                        sourceEpisode,
                        targetDungeonSelection,
                        targetLogicalDifficulty,
                        token))
                {
                    _log($"{channel}:{remote} Rejected locked dungeon progression transition: room={resetRoom.Id} action={resetAction} episode={sourceEpisode} targetDungeon={targetDungeonSelection} targetLogicalDifficulty={targetLogicalDifficulty}");
                    return null;
                }

                if (!DungeonCombatCatalog.HasStage(
                        resetRoom.HdIndex,
                        sourceEpisode,
                        targetDungeonSelection,
                        targetStageSelection))
                {
                    _log($"{channel}:{remote} Rejected unavailable dungeon result target: room={resetRoom.Id} action={resetAction} hd={resetRoom.HdIndex} episode={sourceEpisode} dungeon={targetDungeonSelection} realStage={targetStageSelection}");
                    return null;
                }
                byte responseDungeonSelector;
                byte responseDifficulty;
                byte targetDungeon;
                byte targetStage;
                lock (_dungeonRoomGate)
                {
                    if (resetRoom.OwnerSessionId != session.SessionId
                        || resetRoom.BattleEpisode != sourceEpisode
                        || resetRoom.BattleDungeon != sourceDungeon
                        || resetRoom.BattleStage != sourceStage
                        || resetRoom.Difficulty != sourceWireDifficulty
                        || resetRoom.BattleLogicalDifficulty != sourceLogicalDifficulty
                        || !resetRoom.Battle.SettlementStarted
                        || resetRoom.Battle.ContinuingCharacters.Count != 0
                        || resetRoom.Battle.RewardCommittingCharacters.Count != 0
                        || HasPendingDungeonRewardsLocked(resetRoom)
                        || ((resetAction is DungeonSettlementAction.NextDungeon
                                or DungeonSettlementAction.ChallengeBoss
                                or DungeonSettlementAction.NextDifficulty)
                            && !resetRoom.BossClearAnnounced))
                        return null;
                    // IDA and retail captures map Retry and Challenge
                    // Super-BOSS to mode 1. Mode 2 advances either the normal
                    // dungeon index or, after Super-BOSS, the logical difficulty.
                    // RealStage/ShowStage live at client globals
                    // +0x1E540/+0x1E544; CF6C byte 29 is that same RealStage,
                    // while word 30 is the independent +0x1E528 difficulty.
                    resetRoom.PendingEpisode = sourceEpisode;
                    resetRoom.PendingDungeon = targetDungeonSelection;
                    resetRoom.PendingStage = targetStageSelection;
                    resetRoom.PendingLogicalDifficulty = targetLogicalDifficulty;
                    resetRoom.HasPendingTransition = true;
                    targetDungeon = resetRoom.PendingDungeon;
                    targetStage = resetRoom.PendingStage;
                    responseDungeonSelector = targetDungeon;
                    responseDifficulty = resetAction switch
                    {
                        DungeonSettlementAction.ChallengeBoss =>
                            EncodeDungeonDifficultySelector(targetLogicalDifficulty, superBoss: true),
                        DungeonSettlementAction.NextDifficulty =>
                            EncodeDungeonDifficultySelector(targetLogicalDifficulty, superBoss: false),
                        _ => sourceWireDifficulty
                    };
                    resetRoom.CreateRequestPayload[27] = resetRoom.PendingEpisode;
                    resetRoom.CreateRequestPayload[28] = resetRoom.PendingDungeon;
                    resetRoom.CreateRequestPayload[29] = resetRoom.PendingStage;
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        resetRoom.CreateRequestPayload.AsSpan(30, 2),
                        responseDifficulty);
                    ResetDungeonBattleLocked(resetRoom, beginTransition: true);
                    resetRoom.Battle.RequestedStageIndex = targetStageSelection;
                    resetRoom.Battle.ShowStageNumber = requestedShowStage;
                    foreach (var member in resetRoom.Members.Values)
                    {
                        member.Session.DungeonReady = false;
                        member.Session.DungeonTeamCode = 0;
                    }
                    resetRoom.TransitioningSessionIds.Clear();
                    resetRoom.TransitioningSessionIds.UnionWith(resetRoom.Members.Keys);
                    resetRoom.TransitionDisconnectedSessionIds.Clear();
                    resetRoom.TransitionGameConnectedSessionIds.Clear();
                    resetRoom.TransitionReenteredSessionIds.Clear();
                    resetRoom.TransitionTownReentrySentSessionIds.Clear();
                    resetRoom.TransitionConnectionReentrySentSessionIds.Clear();
                    resetRoom.TransitionStartedUtc = DateTime.UtcNow;
                    resetRoom.SettlementAction = resetAction;
                }
                var resetPayload = BuildDungeonResettingPayload(
                    targetStageSelection, requestedShowStage, responseDifficulty, responseDungeonSelector);
                QueueDungeonBroadcast(session, 0xCF8C, resetPayload, false, "dungeon stage reset");
                _log($"{channel}:{remote} Dungeon result action acknowledged: room={resetRoom.Id} action={resetAction} mode={resetMode} responseReal={targetStageSelection} responseShow={requestedShowStage} sourceWireDifficulty={sourceWireDifficulty} sourceLogicalDifficulty={sourceLogicalDifficulty} responseWireDifficulty={responseDifficulty} targetLogicalDifficulty={targetLogicalDifficulty} responseDungeon={responseDungeonSelector} targetDungeon={targetDungeon} targetStage={targetStage}; transitionMembers={resetRoom.TransitioningSessionIds.Count}, retaining room through the official CF73/CF1D rebuild chain");
                return BuildNativeFrame(frame, 0xCF8C, resetPayload, session);
            }

            case 0xCF15: // REQ_FLYSHOOTING_STAGE_RECORDS -> ANS_FLYSHOOTING_STAGE_RECORDS
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != DungeonStageRecordsPayloadLength)
                    return null;
                var recordsRoom = GetDungeonRoom(session);
                if (recordsRoom is null)
                    return null;
                if (BinaryPrimitives.ReadUInt16LittleEndian(payload) != DungeonEpisodeCount
                    || BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2)) >= DungeonDifficultyCount)
                    return null;
                byte recordsHdIndex;
                byte recordsEpisode;
                byte recordsDungeon;
                byte recordsStage;
                byte recordsDifficulty;
                lock (_dungeonRoomGate)
                {
                    if (!recordsRoom.Members.ContainsKey(session.SessionId))
                        return null;
                    recordsHdIndex = recordsRoom.HdIndex;
                    recordsEpisode = recordsRoom.BattleEpisode;
                    recordsDungeon = recordsRoom.BattleDungeon;
                    recordsStage = recordsRoom.BattleStage;
                    recordsDifficulty = recordsRoom.BattleLogicalDifficulty;
                }
                var stageRecords = await _database.GetDungeonStageLeaderboardAsync(
                    recordsHdIndex, recordsEpisode, recordsDungeon, recordsStage, recordsDifficulty,
                    limit: 10, cancellationToken: token);
                var recordsPayload = BuildDungeonStageRecordsPayload(payload, stageRecords);
                _log($"{channel}:{remote} Dungeon stage leaderboard returned: selectors={recordsHdIndex}/{recordsEpisode}/{recordsDungeon}/{recordsStage}/{recordsDifficulty} records={stageRecords.Count}");
                return BuildNativeFrame(frame, 0xCF16, recordsPayload, session);
            }

            case 0xCF89: // REQ_FLYSHOOTING_END_PVPGAME_INFO -> ANS_FLYSHOOTING_END_PVPGAME_INFO
            {
                if (!string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal)
                    || !session.OnlineTracked
                    || !session.AuxiliaryGameSession
                    || session.Character is null
                    || payload.Length != 4)
                    return null;

                var reportedValue = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                var arenaResult = await CollectArenaRoundResultAsync(session, reportedValue, token);
                if (arenaResult is null)
                    return null;
                _log($"{channel}:{remote} Arena PVP result delivered: room={arenaResult.Value.RoomId} character={session.Character.Id} clientValue={reportedValue} records={arenaResult.Value.RecordCount} complete={arenaResult.Value.AllActiveResultsReceived}");
                return BuildNativeFrame(
                    frame,
                    0xCF8A,
                    arenaResult.Value.Payload,
                    session);
            }

            case 0xCF97: // REQ_FLYSHOOTING_END_PVPGAME -> ANS_FLYSHOOTING_END_PVPGAME
            {
                if (!string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal)
                    || !session.OnlineTracked
                    || !session.AuxiliaryGameSession
                    || session.Character is null
                    || payload.Length != 0)
                    return null;
                if (!TryMarkArenaRoundEnding(session, out var arenaRoomId))
                    return null;
                _log($"{channel}:{remote} Arena PVP round entered result phase: room={arenaRoomId} character={session.Character.Id}");
                return BuildNativeFrame(frame, 0xCF98, [], session);
            }

            case 0xD036: // REQ_ARENA_MODE_MAP_CHANGE -> ANS_ARENA_MODE_MAP_CHANGE
            {
                if (!string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal)
                    || !session.OnlineTracked
                    || !session.AuxiliaryGameSession
                    || session.Character is null
                    || payload.Length != 4)
                    return null;
                var selectedMode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var selectedMap = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                if (IsEntertainmentSession(channel, session))
                {
                    if (!TrySetEntertainmentSelection(session, selectedMode, selectedMap))
                        return null;
                    var entertainmentSelectionPayload = payload.ToArray();
                    QueueEntertainmentBroadcast(
                        session,
                        0xD037,
                        entertainmentSelectionPayload,
                        false,
                        "entertainment selection");
                    return BuildNativeFrame(
                        frame,
                        0xD037,
                        entertainmentSelectionPayload,
                        session);
                }
                if (!TrySetArenaModeAndMap(session, selectedMode, selectedMap))
                    return null;

                var (resolvedMode, resolvedMap) = ArenaProtocol.ResolveGameSelectors(
                    selectedMode,
                    selectedMap);
                var arenaSelectionPayload = new byte[4];
                BinaryPrimitives.WriteUInt16LittleEndian(
                    arenaSelectionPayload.AsSpan(0, 2),
                    resolvedMode);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    arenaSelectionPayload.AsSpan(2, 2),
                    resolvedMap);
                QueueArenaBroadcast(
                    session,
                    0xD037,
                    arenaSelectionPayload,
                    false,
                    "arena mode and map selection");
                _log($"{channel}:{remote} Arena mode/map synchronized: room={session.ArenaRoomId} requested={selectedMode}/{selectedMap} resolved={resolvedMode}/{resolvedMap}");
                return BuildNativeFrame(frame, 0xD037, arenaSelectionPayload, session);
            }

            case 0xCF99: // REQ_FLYSHOOTING_SURRENDER_ABUSER
                if (!session.OnlineTracked || session.Character is null || payload.Length != 0)
                    return null;
                _log($"{channel}:{remote} 地宫放弃滥用检查已确认；返回 CF9A 空结果");
                return BuildNativeFrame(frame, 0xCF9A, [], session);

            case 0xCF95: // handled before native-dungeon routing
                return await HandleDungeonRevivalRetryAsync(frame, channel, remote, session, payload, token);

            case 0xD00F: // SEND_MULTICASTING_GAMEEVENT -> RECV_MULTICASTING_GAMEEVENT
            {
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                var expectedGameEventLength = string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal)
                    ? ArenaProtocol.GameEventRequestLength
                    : DungeonProtocol.ObjectEventRequestLength;
                if (payload.Length != expectedGameEventLength)
                {
                    _log($"{channel}:{remote} Game-event length invalid: expected={expectedGameEventLength} actual={payload.Length}; no response");
                    return null;
                }

                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (!session.AuxiliaryGameSession
                        || !ArenaProtocol.TryParseGameEvent(payload, out var arenaEvent)
                        || !TryBuildArenaGameEventResult(
                            session,
                            arenaEvent,
                            out var arenaEventRoomId,
                            out var arenaEventPayload,
                            out var arenaHpBefore,
                            out var arenaHpAfter,
                            out var arenaScoreBefore,
                            out var arenaScoreAfter))
                        return null;
                    QueueArenaBroadcast(
                        session,
                        0xD010,
                        arenaEventPayload,
                        false,
                        "arena object event");
                    _log($"{channel}:{remote} Arena game event resolved: room={arenaEventRoomId} character={session.Character.Id} event={arenaEvent.EventCode} primary={arenaEvent.PrimaryUid} playerDamage={arenaEvent.PlayerDamage} targetDamage={arenaEvent.TargetDamage} hp={arenaHpBefore}->{arenaHpAfter} score={arenaScoreBefore}->{arenaScoreAfter}");
                    return BuildNativeFrame(frame, 0xD010, arenaEventPayload, session);
                }

                var eventCode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var reportedDamage = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                var targetVectorIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
                var collisionActorUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                ushort damage = 0;
                if (eventCode == 10)
                {
                    var damageRoom = GetDungeonRoom(session);
                    var damageBattle = damageRoom?.Battle;
                    // Retail sub_674A00 passes the collision object's +0x140
                    // damage value, target-vector index and temporary object
                    // UID to sub_6EA320. These become request +2/+4/+6. The
                    // temporary collision UID is not an SMMO monster UID.
                    if (damageRoom is null
                        || damageBattle is null
                        || !damageRoom.Started)
                    {
                        _log($"{channel}:{remote} Rejected dungeon collision-damage state: room={damageRoom?.Id ?? 0} damage={reportedDamage} targetIndex={targetVectorIndex} actor={collisionActorUid}");
                        return null;
                    }

                    lock (_dungeonRoomGate)
                    {
                        if (!ReferenceEquals(damageRoom.Battle, damageBattle)
                            || damageBattle.State != DungeonBattleState.Active
                            || !damageRoom.Members.ContainsKey(session.SessionId)
                            || damageBattle.DeadCharacters.Contains(session.Character.Id))
                        {
                            _log($"{channel}:{remote} Rejected dungeon collision damage after battle-state validation: room={damageRoom.Id} character={session.Character.Id} sameInstance={ReferenceEquals(damageRoom.Battle, damageBattle)} state={damageBattle.State} dead={damageBattle.DeadCharacters.Contains(session.Character.Id)}");
                            return null;
                        }

                        damage = (ushort)Math.Min(
                            Math.Min(reportedDamage, session.Character.CurrentHp),
                            ushort.MaxValue);
                        if (damage > 0)
                        {
                            session.Character.CurrentHp = Math.Max(0, session.Character.CurrentHp - damage);
                            if (session.Character.CurrentHp == 0)
                            {
                                damageBattle.DeadCharacters.Add(session.Character.Id);
                                damageBattle.RemainingContinues.TryAdd(
                                    session.Character.Id,
                                    DungeonInitialContinueCount);
                            }
                        }
                    }
                }
                if (damage > 0)
                {
                    var saved = session.AccountId > 0
                                && !string.IsNullOrWhiteSpace(session.Username)
                                && await _database.SaveCharacterRuntimeStateAsync(
                                    session.AccountId,
                                    session.Character.Id,
                                    session.SessionId,
                                    CreateRuntimeState(session.Character, session.ChannelId),
                                    token);
                    _log($"{channel}:{remote} Dungeon player damage applied: room={session.DungeonRoomId} map={GetDungeonRoom(session)?.SelectedMapIndex?.ToString() ?? "none"} characterId={session.Character.Id} reportedDamage={reportedDamage} targetIndex={targetVectorIndex} actor={collisionActorUid} damage={damage} hp={session.Character.CurrentHp}/{session.Character.MaxHp} persisted={saved}");
                }
                var objectEventPayload = eventCode == 10
                    ? BuildDungeonGameEventPayload(
                        session.Character,
                        eventCode,
                        reportedDamage,
                        damage)
                    : DungeonProtocol.BuildObjectEvent(payload);
                QueueDungeonBroadcast(session, 0xD010, objectEventPayload, false, "dungeon object event");
                _log($"{channel}:{remote} Dungeon object event synchronized: characterId={session.Character.Id} event={eventCode} reportedDamage={reportedDamage} targetIndex={targetVectorIndex} actor={collisionActorUid} damage={damage} hp={session.Character.CurrentHp}/{session.Character.MaxHp} mode={(eventCode == 10 ? "authoritative-hp-result" : "retail-echo")}");
                return BuildNativeFrame(frame, 0xD010, objectEventPayload, session);
            }

            case 0xCF73: // REQ_GAMEROOM_LEAVE -> GAMEROOM_LEAVE_USER
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未建立世界会话的离开房间请求");
                    return null;
                }
                if (payload.Length != 0)
                {
                    _log($"{channel}:{remote} 离开房间请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (!session.AuxiliaryGameSession)
                        return null;
                    if (IsEntertainmentSession(channel, session))
                    {
                        var entertainmentRoomId = session.EntertainmentRoomId;
                        var entertainmentOwner = FindEntertainmentOwnerAfterLeave(session)
                            ?? session.Character;
                        var entertainmentLeavePayload = BuildGameRoomLeavePayload(
                            session.Character,
                            entertainmentOwner);
                        QueueEntertainmentBroadcast(
                            session,
                            0xCF74,
                            entertainmentLeavePayload,
                            false,
                            "entertainment member leave");
                        RemoveEntertainmentRoomMember(session);
                        QueueEntertainmentLobbyRoomListRefresh(session, "entertainment member left");
                        _log($"{channel}:{remote} Entertainment room leave: room={entertainmentRoomId} character={session.Character.Name}");
                        return BuildNativeFrame(frame, 0xCF74, entertainmentLeavePayload, session);
                    }
                    var arenaRoomId = session.ArenaRoomId;
                    var arenaOwner = FindArenaRoomOwnerAfterLeave(session) ?? session.Character;
                    var arenaLeavePayload = BuildGameRoomLeavePayload(session.Character, arenaOwner);
                    QueueArenaBroadcast(session, 0xCF74, arenaLeavePayload, false, "arena member leave");
                    RemoveArenaRoomMember(session);
                    QueueArenaLobbyRoomListRefresh(session, "arena member left");
                    _log($"{channel}:{remote} Arena room leave completed: room={arenaRoomId} character={session.Character.Name} nextOwner={arenaOwner.Name}");
                    return BuildNativeFrame(frame, 0xCF74, arenaLeavePayload, session);
                }
                if (TryGetDungeonTransition(session, out var leaveTransitionRoomId, out var leaveTransitionAction))
                {
                    // Retail stage transitions send CF73 and CF1D as transport
                    // teardown notifications before rebuilding the same room.
                    // CF74 removes the local room identity, so it must only be
                    // returned for a real leave/return-to-village request.
                    _log($"{channel}:{remote} Dungeon transition preserved room: room={leaveTransitionRoomId} characterId={session.Character.Id} action={leaveTransitionAction}; CF73 notification suppressed");
                    return null;
                }
                await RestoreDungeonVitalsAsync(session, false, token);
                var leaveOwner = FindDungeonRoomOwnerAfterLeave(session) ?? session.Character;
                var leavePayload = BuildGameRoomLeavePayload(session.Character, leaveOwner);
                QueueDungeonBroadcast(session, 0xCF74, leavePayload, false, "dungeon member leave");
                var leftRoomId = session.DungeonRoomId;
                _log($"{channel}:{remote} Dungeon result action committed: room={leftRoomId} characterId={session.Character.Id} action={DungeonSettlementAction.ReturnVillage}; returning CF74 and removing room membership");
                RemoveDungeonRoomMember(session);
                return BuildNativeFrame(frame, 0xCF74, leavePayload, session);

            case 0xCF8D: // REQ_WANT_USER_OUT (owner kicks a room member)
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != 4)
                    return null;
                var targetUid = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        ConnectionSession? entertainmentTarget;
                        CharacterRecord? entertainmentOwner;
                        lock (_entertainmentRoomGate)
                        {
                            if (!_entertainmentRooms.TryGetValue(session.EntertainmentRoomId, out var room)
                                || room.OwnerSessionId != session.SessionId
                                || room.Started)
                                return null;
                            entertainmentTarget = room.Members.Values.FirstOrDefault(member =>
                                member.SessionId != session.SessionId
                                && member.Character is { } character
                                && GetSceneEntityId(character) == targetUid);
                            entertainmentOwner = room.Members.GetValueOrDefault(room.OwnerSessionId)?.Character;
                        }
                        if (entertainmentTarget?.Character is null || entertainmentOwner is null)
                            return null;
                        var entertainmentKickPayload = BuildGameRoomLeavePayload(
                            entertainmentTarget.Character,
                            entertainmentOwner);
                        QueueEntertainmentBroadcast(
                            session,
                            0xCF74,
                            entertainmentKickPayload,
                            true,
                            "entertainment owner kicked member");
                        RemoveEntertainmentRoomMember(entertainmentTarget, session);
                        QueueEntertainmentLobbyRoomListRefresh(session, "entertainment member kicked");
                        return null;
                    }
                    ConnectionSession? arenaTarget;
                    CharacterRecord? arenaOwner;
                    int arenaRoomId;
                    lock (_arenaRoomGate)
                    {
                        if (!_arenaRooms.TryGetValue(session.ArenaRoomId, out var room)
                            || room.OwnerSessionId != session.SessionId
                            || room.Started)
                            return null;
                        arenaTarget = room.Members.Values.FirstOrDefault(member =>
                            member.SessionId != session.SessionId
                            && member.Character is { } character
                            && GetSceneEntityId(character) == targetUid);
                        arenaOwner = room.Members.GetValueOrDefault(room.OwnerSessionId)?.Character;
                        arenaRoomId = room.Id;
                    }
                    if (arenaTarget?.Character is null || arenaOwner is null)
                    {
                        _log($"{channel}:{remote} Rejected arena member kick: room={session.ArenaRoomId} owner={session.Character.Id} target={targetUid}");
                        return null;
                    }

                    var arenaKickPayload = BuildGameRoomLeavePayload(arenaTarget.Character, arenaOwner);
                    QueueArenaBroadcast(session, 0xCF74, arenaKickPayload, true, "arena owner kicked member");
                    RemoveArenaRoomMember(arenaTarget, session);
                    QueueArenaLobbyRoomListRefresh(session, "arena member kicked");
                    _log($"{channel}:{remote} Arena owner kicked member: room={arenaRoomId} owner={session.Character.Id} target={targetUid}");
                    return null;
                }
                ConnectionSession? targetSession;
                CharacterRecord? ownerCharacter;
                int roomId;
                lock (_dungeonRoomGate)
                {
                    if (!_dungeonRooms.TryGetValue(session.DungeonRoomId, out var room)
                        || room.OwnerSessionId != session.SessionId
                        || room.Started)
                        return null;
                    targetSession = room.Members.Values
                        .Select(member => member.Session)
                        .FirstOrDefault(member =>
                            member.SessionId != session.SessionId
                            && member.Character is { } character
                            && character.Id == targetUid);
                    ownerCharacter = room.Members.GetValueOrDefault(room.OwnerSessionId)?.Session.Character;
                    roomId = room.Id;
                }
                if (targetSession?.Character is null || ownerCharacter is null)
                {
                    _log($"{channel}:{remote} Rejected dungeon member kick: room={session.DungeonRoomId} owner={session.Character.Id} target={targetUid}");
                    return null;
                }

                var kickPayload = BuildGameRoomLeavePayload(targetSession.Character, ownerCharacter);
                QueueDungeonBroadcast(session, 0xCF74, kickPayload, true, "dungeon owner kicked member");
                RemoveDungeonRoomMember(targetSession);
                _log($"{channel}:{remote} Dungeon owner kicked member: room={roomId} owner={session.Character.Id} target={targetUid}");
                return null;
            }

            case 0xCF1D: // REQ_GAME_DISCONNECTION -> ANS_GAME_DISCONNECTION
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (!session.OnlineTracked
                        || !session.AuxiliaryGameSession
                        || session.Character is null
                        || payload.Length != 0)
                        return null;
                    if (IsEntertainmentSession(channel, session))
                    {
                        var previousEntertainmentRoomId = session.EntertainmentRoomId;
                        QueueEntertainmentDisconnectNotification(session);
                        _log($"{channel}:{remote} Entertainment disconnection acknowledged: room={previousEntertainmentRoomId} character={session.Character.Name}");
                        return BuildNativeFrame(frame, 0xCF1E, BuildGameDisconnectionPayload(200), session);
                    }
                    var previousArenaRoomId = session.ArenaRoomId;
                    QueueArenaDisconnectNotification(session);
                    _log($"{channel}:{remote} Arena room/lobby disconnection acknowledged: room={previousArenaRoomId} character={session.Character.Name}");
                    return BuildNativeFrame(frame, 0xCF1E, BuildGameDisconnectionPayload(200), session);
                }
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未建立世界会话的地宫断开请求");
                    return null;
                }
                if (payload.Length != 0)
                {
                    _log($"{channel}:{remote} 地宫断开请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }
                if (TryGetDungeonTransition(session, out var disconnectTransitionRoomId, out var disconnectTransitionAction))
                {
                    MarkDungeonTransitionDisconnected(session);
                    _log($"{channel}:{remote} Dungeon transition disconnect acknowledged without clearing room: room={disconnectTransitionRoomId} characterId={session.Character.Id} action={disconnectTransitionAction}");
                    return BuildNativeFrame(frame, 0xCF1E, BuildGameDisconnectionPayload(200), session);
                }
                await RestoreDungeonVitalsAsync(session, false, token);
                QueueDungeonDisconnectNotification(session);
                RemoveDungeonRoomMember(session);
                _log($"{channel}:{remote} 地宫断开完成并清理房间成员，返回村庄：characterId={session.Character.Id}");
                return BuildNativeFrame(frame, 0xCF1E, BuildGameDisconnectionPayload(200), session);

            case 0xCFE5: // REQ_ENTERTAINMENT_GAME_DATA -> ANS_ENTERTAINMENT_GAME_DATA
            {
                var entertainmentStartReason = "invalid-session";
                byte[] entertainmentGameDataPage = [];
                if (!IsEntertainmentSession(channel, session)
                    || !session.OnlineTracked
                    || !session.AuxiliaryGameSession
                    || session.Character is null
                    || payload.Length != 0
                    || !TryBeginEntertainmentGame(
                        session,
                        out entertainmentGameDataPage,
                        out entertainmentStartReason))
                {
                    _log($"{channel}:{remote} Entertainment CFE5 rejected: room={session.EntertainmentRoomId} reason={entertainmentStartReason}");
                    return null;
                }
                if (entertainmentStartReason == "ok")
                    QueueEntertainmentLobbyRoomListRefresh(session, "entertainment game started");
                return BuildNativeFrame(frame, 0xCFE6, entertainmentGameDataPage, session);
            }

            case 0xD003: // REQ_PICNIC_SELECT_GAME -> ANS_PICNIC_SELECT_GAME
            {
                if (!IsEntertainmentSession(channel, session)
                    || !session.OnlineTracked
                    || !session.AuxiliaryGameSession
                    || session.Character is null
                    || payload.Length != 4
                    || !TrySetEntertainmentPicnicLives(
                        session,
                        BinaryPrimitives.ReadUInt32LittleEndian(payload),
                        out var picnicLivesUid,
                        out var picnicLives))
                    return null;
                var responsePayload = EntertainmentProtocol.BuildPicnicPlayerState(
                    picnicLivesUid,
                    picnicLives);
                QueueEntertainmentBroadcast(
                    session,
                    0xD004,
                    responsePayload,
                    false,
                    "entertainment picnic remaining games");
                var responseFrame = BuildNativeFrame(frame, 0xD004, responsePayload, session);
                var nextGameDataPage = TakeNextEntertainmentGameDataPage(session);
                return nextGameDataPage is null
                    ? responseFrame
                    : CombineNativeFrames(
                        responseFrame,
                        BuildNativeFrame(frame, 0xCFE6, nextGameDataPage, session));
            }

            case 0xD005: // REQ_PICNIC_PROGRESS -> ANS_PICNIC_PROGRESS
            {
                if (!IsEntertainmentSession(channel, session)
                    || !session.OnlineTracked
                    || !session.AuxiliaryGameSession
                    || session.Character is null
                    || payload.Length != 4
                    || !TryApplyEntertainmentPicnicProgress(
                        session,
                        BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2)),
                        BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2)),
                        out var picnicScoreUid,
                        out var picnicSynchronizedValue))
                    return null;
                var responsePayload = EntertainmentProtocol.BuildPicnicPlayerState(
                    picnicScoreUid,
                    picnicSynchronizedValue);
                QueueEntertainmentBroadcast(
                    session,
                    0xD006,
                    responsePayload,
                    false,
                    "entertainment picnic score");
                var responseFrame = BuildNativeFrame(frame, 0xD006, responsePayload, session);
                var nextGameDataPage = TakeNextEntertainmentGameDataPage(session);
                return nextGameDataPage is null
                    ? responseFrame
                    : CombineNativeFrames(
                        responseFrame,
                        BuildNativeFrame(frame, 0xCFE6, nextGameDataPage, session));
            }

            case 0xD007: // REQ_PICNIC_CELL -> ANS_PICNIC_CELL
            {
                if (!IsEntertainmentSession(channel, session)
                    || !session.OnlineTracked
                    || !session.AuxiliaryGameSession
                    || session.Character is null
                    || payload.Length != 4
                    || !TryBuildEntertainmentPicnicCellState(
                        session,
                        payload[0],
                        payload[1],
                        payload[2],
                        payload[3],
                        out var responsePayload))
                    return null;
                QueueEntertainmentBroadcast(
                    session,
                    0xD008,
                    responsePayload,
                    false,
                    "entertainment picnic cell state");
                return BuildNativeFrame(frame, 0xD008, responsePayload, session);
            }

            case 0xCF81: // REQ_CONTINUE_GAME / REQ_ENTERTAINMENT_COUNTDOWN -> CF82
            {
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (!IsEntertainmentSession(channel, session)
                        || !session.OnlineTracked
                        || session.Character is null
                        || payload.Length != 0
                        || !IsStartedEntertainmentRoomMember(session))
                        return null;
                    return BuildNativeFrame(frame, 0xCF82, [], session);
                }

                if (!string.Equals(channel, "WorldAdapter", StringComparison.Ordinal)
                    || !session.OnlineTracked
                    || session.Character is null
                    || payload.Length != 0)
                    return null;

                byte remainingContinues;
                var continueRoom = GetDungeonRoom(session);
                lock (_dungeonRoomGate)
                {
                    if (continueRoom is null
                        || continueRoom.Battle.State != DungeonBattleState.Active
                        || !continueRoom.Members.ContainsKey(session.SessionId)
                        || !continueRoom.Battle.DeadCharacters.Contains(session.Character.Id))
                        return null;

                    if (!continueRoom.Battle.RemainingContinues.TryGetValue(
                            session.Character.Id, out remainingContinues))
                    {
                        remainingContinues = DungeonInitialContinueCount;
                        continueRoom.Battle.RemainingContinues[session.Character.Id] =
                            remainingContinues;
                    }
                }

                var continueCountPayload = new byte[DungeonContinueCountResponsePayloadLength];
                BinaryPrimitives.WriteUInt16LittleEndian(
                    continueCountPayload.AsSpan(0, 2),
                    GetSceneEntityId(session.Character));
                BinaryPrimitives.WriteUInt16LittleEndian(
                    continueCountPayload.AsSpan(2, 2),
                    remainingContinues);
                QueueDungeonBroadcast(
                    session,
                    0xCF82,
                    continueCountPayload,
                    false,
                    "dungeon continue count");
                _log($"{channel}:{remote} Dungeon continue count synchronized: room={continueRoom.Id} character={session.Character.Id} uid={GetSceneEntityId(session.Character)} remaining={remainingContinues}");
                return BuildNativeFrame(frame, 0xCF82, continueCountPayload, session);
            }

            case 0xCF83: // REQ_FLYSHOOTING_CONTINUE_GAME / REQ_ENTERTAINMENT_VALUE -> CF84
            {
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (!IsEntertainmentSession(channel, session)
                        || !session.OnlineTracked
                        || session.Character is null
                        || payload.Length != 4)
                        return null;
                    var entertainmentValueUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                    var entertainmentValue = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                    if (!TryUpdateEntertainmentValue(session, entertainmentValueUid, entertainmentValue))
                        return null;
                    var entertainmentValuePayload = payload.ToArray();
                    QueueEntertainmentBroadcast(
                        session,
                        0xCF84,
                        entertainmentValuePayload,
                        false,
                        "entertainment game value");
                    return BuildNativeFrame(frame, 0xCF84, entertainmentValuePayload, session);
                }

                if (!string.Equals(channel, "WorldAdapter", StringComparison.Ordinal)
                    || !session.OnlineTracked
                    || session.Character is null
                    || payload.Length != DungeonContinueRequestPayloadLength)
                    return null;

                var continueMode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var requestedContinueCost = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                DungeonRoom dungeonContinueRoom;
                DungeonBattleInstance dungeonContinueBattle;
                byte remainingContinues;
                ushort expectedContinueCost;
                lock (_dungeonRoomGate)
                {
                    var candidateRoom = _dungeonRooms.GetValueOrDefault(session.DungeonRoomId);
                    if (continueMode is not (0 or 1)
                        || candidateRoom is null
                        || candidateRoom.Battle.State != DungeonBattleState.Active
                        || !candidateRoom.Members.ContainsKey(session.SessionId)
                        || !candidateRoom.Battle.DeadCharacters.Contains(session.Character.Id)
                        || !candidateRoom.Battle.RemainingContinues.TryGetValue(
                            session.Character.Id, out remainingContinues)
                        || remainingContinues == 0
                        || !TryGetDungeonContinueCost(
                            candidateRoom.HdIndex,
                            candidateRoom.BattleEpisode,
                            out expectedContinueCost)
                        || requestedContinueCost != expectedContinueCost
                        || !candidateRoom.Battle.ContinuingCharacters.Add(session.Character.Id))
                    {
                        _log($"{channel}:{remote} Rejected dungeon continue request: room={session.DungeonRoomId} character={session.Character.Id} mode={continueMode} requestedCost={requestedContinueCost} expectedCost={(candidateRoom is not null && TryGetDungeonContinueCost(candidateRoom.HdIndex, candidateRoom.BattleEpisode, out var loggedCost) ? loggedCost : 0)} dead={candidateRoom?.Battle.DeadCharacters.Contains(session.Character.Id) == true} remaining={(candidateRoom?.Battle.RemainingContinues.GetValueOrDefault(session.Character.Id)).GetValueOrDefault()}");
                        return null;
                    }

                    dungeonContinueRoom = candidateRoom;
                    dungeonContinueBattle = candidateRoom.Battle;
                }

                // The retail CF84 consumer writes payload +0x04/+0x06
                // directly into the character HP/MP setters. Persist exactly
                // those half-vital values so the next authoritative D00F hit
                // cannot jump from the client's half HP to a hidden full HP.
                var restoredHp = Math.Max(1, (session.Character.MaxHp + 1) / 2);
                var restoredMp = Math.Max(1, (session.Character.MaxMp + 1) / 2);
                (bool Success, string Error, long Hans, byte RevivalUseCount, int CurrentHp, int CurrentMp)
                    consumedContinue;
                try
                {
                    consumedContinue = await _database.ConsumeDungeonContinueAsync(
                        session.AccountId,
                        session.Character.Id,
                        session.SessionId,
                        continueMode,
                        expectedContinueCost,
                        restoredHp,
                        restoredMp,
                        token);
                }
                catch
                {
                    lock (_dungeonRoomGate)
                        dungeonContinueBattle.ContinuingCharacters.Remove(session.Character.Id);
                    throw;
                }

                if (!consumedContinue.Success)
                {
                    lock (_dungeonRoomGate)
                        dungeonContinueBattle.ContinuingCharacters.Remove(session.Character.Id);
                    _log($"{channel}:{remote} Dungeon continue payment rejected: room={dungeonContinueRoom.Id} character={session.Character.Id} mode={continueMode} cost={expectedContinueCost} error={consumedContinue.Error}");
                    return null;
                }

                lock (_dungeonRoomGate)
                {
                    if (!_dungeonRooms.TryGetValue(dungeonContinueRoom.Id, out var currentRoom)
                        || !ReferenceEquals(currentRoom, dungeonContinueRoom)
                        || !ReferenceEquals(currentRoom.Battle, dungeonContinueBattle)
                        || currentRoom.Battle.State != DungeonBattleState.Active
                        || !currentRoom.Members.ContainsKey(session.SessionId)
                        || !dungeonContinueBattle.DeadCharacters.Remove(session.Character.Id)
                        || !dungeonContinueBattle.ContinuingCharacters.Remove(session.Character.Id))
                    {
                        dungeonContinueBattle.ContinuingCharacters.Remove(session.Character.Id);
                        _log($"{channel}:{remote} Dungeon continue state changed after atomic payment: room={dungeonContinueRoom.Id} character={session.Character.Id}; response suppressed");
                        return null;
                    }

                    dungeonContinueBattle.RemainingContinues[session.Character.Id] =
                        checked((byte)(remainingContinues - 1));
                    session.Character.Hans = consumedContinue.Hans;
                    session.Character.RevivalUseCount = consumedContinue.RevivalUseCount;
                    session.Character.CurrentHp = consumedContinue.CurrentHp;
                    session.Character.CurrentMp = consumedContinue.CurrentMp;
                }

                var continueResultPayload = BuildDungeonContinueApplyPayload(
                    session.Character,
                    continueMode == 0 ? (ushort)20 : (ushort)60);
                QueueDungeonBroadcast(
                    session,
                    0xCF84,
                    continueResultPayload,
                    false,
                    "dungeon continue result");
                _log($"{channel}:{remote} Dungeon continue completed: room={dungeonContinueRoom.Id} character={session.Character.Id} uid={GetSceneEntityId(session.Character)} mode={continueMode} result={(continueMode == 0 ? 20 : 60)} cost={expectedContinueCost} remaining={remainingContinues - 1} hp={session.Character.CurrentHp}/{session.Character.MaxHp} mp={session.Character.CurrentMp}/{session.Character.MaxMp} cf84Aux=(0,0) hans={session.Character.Hans} revivalUses={session.Character.RevivalUseCount}");
                return BuildNativeFrame(frame, 0xCF84, continueResultPayload, session);
            }

            case 0xCF85: // REQ_ENTERTAINMENT_CONTINUE -> ANS_END_GAME_INFO
            {
                if (!IsEntertainmentSession(channel, session)
                    || !session.OnlineTracked
                    || session.Character is null
                    || payload.Length != 0)
                    return null;
                var entertainmentEndPayload = BuildEntertainmentEndGamePayload(session);
                return entertainmentEndPayload is null
                    ? null
                    : BuildNativeFrame(frame, 0xCF86, entertainmentEndPayload, session);
            }

            case 0xCF7F: // REQ_GAME_START -> ANS_GAME_START
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未建立世界会话的地宫开始请求");
                    return null;
                }
                if (payload.Length != 0)
                {
                    _log($"{channel}:{remote} 地宫开始请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (!IsStartedEntertainmentRoomMember(session))
                            return null;
                        QueueEntertainmentBroadcast(
                            session,
                            0xCF80,
                            [],
                            false,
                            "entertainment game loading start");
                        return BuildNativeFrame(frame, 0xCF80, [], session);
                    }
                    var arenaStartReason = session.AuxiliaryGameSession
                        ? string.Empty
                        : "not-an-arena-session";
                    if (!session.AuxiliaryGameSession
                        || !TryStartArenaRoom(session, out arenaStartReason))
                    {
                        _log($"{channel}:{remote} Arena start rejected: room={session.ArenaRoomId} owner={session.Character.Name} reason={arenaStartReason}");
                        return null;
                    }

                    QueueArenaBroadcast(session, 0xCF80, [], false, "arena game start");
                    QueueArenaLobbyRoomListRefresh(session, "arena game started");
                    _log($"{channel}:{remote} Arena game start synchronized: room={session.ArenaRoomId} owner={session.Character.Id}");
                    return BuildNativeFrame(frame, 0xCF80, [], session);
                }
                if (!CanStartDungeonRoom(session))
                {
                    // Retail sends CF7F from every client's local loading loop.
                    // The owner starts the room once; members that receive that
                    // start then send their own CF7F and require a matching CF80.
                    if (!IsStartedDungeonRoomMember(session))
                    {
                        _log($"{channel}:{remote} 鍦板寮€濮嬭鎷掔粷锛歳oom={session.DungeonRoomId}锛岃姹傝€呬笉鏄埧涓汇€佹埧闂翠笉瀛樺湪鎴栦粛鏈夋垚鍛樻湭鍑嗗");
                        return null;
                    }

                    _log($"{channel}:{remote} 鍦板鎴愬憳鍔犺浇寮€濮嬪凡纭锛歳oom={session.DungeonRoomId} member={session.Character.Id}");
                    QueueUserAutoHealing(session, session, "restore dungeon HP/MP after member CF80");
                    return BuildNativeFrame(frame, 0xCF80, [], session);
                }
                var startingDungeonRoom = GetDungeonRoom(session);
                if (startingDungeonRoom is null)
                    return null;
                var startingEpisode = startingDungeonRoom.HasPendingTransition
                    ? startingDungeonRoom.PendingEpisode
                    : startingDungeonRoom.BattleEpisode;
                var startingDungeon = startingDungeonRoom.HasPendingTransition
                    ? startingDungeonRoom.PendingDungeon
                    : startingDungeonRoom.BattleDungeon;
                var startingStage = startingDungeonRoom.HasPendingTransition
                    ? startingDungeonRoom.PendingStage
                    : startingDungeonRoom.BattleStage;
                var startingLogicalDifficulty = startingDungeonRoom.HasPendingTransition
                    ? startingDungeonRoom.PendingLogicalDifficulty
                    : startingDungeonRoom.BattleLogicalDifficulty;
                if (!await CanDungeonRoomMembersEnterSelectionAsync(
                        startingDungeonRoom,
                        startingEpisode,
                        startingDungeon,
                        startingLogicalDifficulty,
                        token)
                    || !DungeonCombatCatalog.HasStage(
                        startingDungeonRoom.HdIndex,
                        startingEpisode,
                        startingDungeon,
                        startingStage)
                    || !CanStartDungeonRoom(session))
                {
                    _log($"{channel}:{remote} Rejected dungeon start after member access validation: room={session.DungeonRoomId} episode={startingEpisode} dungeon={startingDungeon} stage={startingStage}");
                    return null;
                }
                await RestoreDungeonVitalsAsync(session, true, token);
                MarkDungeonRoomStarted(session);
                await QueueDungeonStartAsync(session, token);
                QueueDungeonAutoHealing(session, "restore dungeon HP/MP after CF80");
                _log($"{channel}:{remote} 鍦板寮€濮嬪凡骞挎挱锛歳oom={session.DungeonRoomId} owner={session.Character.Id} hp={session.Character.CurrentHp}/{session.Character.MaxHp} mp={session.Character.CurrentMp}/{session.Character.MaxMp}");
                return BuildNativeFrame(frame, 0xCF80, [], session);

            case 0xCF93: // REQ_USE_QUICKSLOT_ITEM -> ANS_USE_QUICKSLOT_ITEM
            {
                if (!string.Equals(channel, "WorldAdapter", StringComparison.Ordinal))
                    return null;

                var requestedSlotValue = payload.Length == DungeonQuickItemUseRequestPayloadLength
                    ? BinaryPrimitives.ReadUInt32LittleEndian(payload)
                    : uint.MaxValue;
                var requestedSlot = requestedSlotValue <= 5 ? checked((byte)requestedSlotValue) : byte.MaxValue;
                var quickItemRoom = GetDungeonRoom(session);
                var quickItemBattle = quickItemRoom?.Battle;
                CharacterQuickSlotRecord? selectedQuickSlot = null;
                ShopCatalogItem? selectedQuickItem = null;
                ConnectionSession[] affectedMembers = [];

                if (session.OnlineTracked
                    && session.Character is not null
                    && payload.Length == DungeonQuickItemUseRequestPayloadLength
                    && requestedSlot <= 5
                    && quickItemRoom is not null
                    && quickItemBattle is not null)
                {
                    lock (_dungeonRoomGate)
                    {
                        var expansionActive = SkillSlotExpansionTime.TryDecode(
                                                  session.Character.QuickSlotExpansionExpires,
                                                  out var expansionExpiration)
                                              && expansionExpiration > DateTime.Now;
                        selectedQuickSlot = session.Character.QuickSlots.SingleOrDefault(
                            slot => slot.Slot == requestedSlot);
                        if (ReferenceEquals(quickItemRoom.Battle, quickItemBattle)
                            && quickItemBattle.State == DungeonBattleState.Active
                            && quickItemRoom.Members.ContainsKey(session.SessionId)
                            && !quickItemBattle.DeadCharacters.Contains(session.Character.Id)
                            && (requestedSlot < 3 || expansionActive)
                            && selectedQuickSlot is not null
                            && ShopCatalog.TryGet(selectedQuickSlot.ItemCode, out selectedQuickItem)
                            && selectedQuickItem.Category is 14 or 21
                            && selectedQuickItem.QuickUsable)
                        {
                            affectedMembers = (selectedQuickItem.QuickAffectsTeam
                                    ? quickItemRoom.Members.Values
                                        .Select(member => member.Session)
                                        .Where(member => member.Character is not null
                                            && !quickItemBattle.DeadCharacters.Contains(member.Character.Id))
                                    : [session])
                                .ToArray();
                        }
                    }
                }

                var responseItemCode = selectedQuickSlot?.ItemCode ?? 0u;
                if (selectedQuickSlot is null
                    || selectedQuickItem is null
                    || affectedMembers.Length == 0
                    || session.Character is null)
                {
                    _log($"{channel}:{remote} Dungeon quick-item use rejected before persistence: room={session.DungeonRoomId} character={session.Character?.Id ?? 0} slot={requestedSlotValue} item={responseItemCode} state={quickItemBattle?.State.ToString() ?? "none"}");
                    return BuildNativeFrame(
                        frame,
                        0xCF94,
                        BuildDungeonQuickItemUsePayload(
                            session.Character is null ? (ushort)0 : GetSceneEntityId(session.Character),
                            requestedSlotValue <= ushort.MaxValue ? checked((ushort)requestedSlotValue) : ushort.MaxValue,
                            responseItemCode,
                            0,
                            0),
                        session);
                }

                var targets = affectedMembers.Select(member => new DungeonQuickItemTarget(
                    member.AccountId,
                    member.Character!.Id,
                    member.SessionId)).ToArray();
                var consumeResult = await _database.ConsumeDungeonQuickItemAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    selectedQuickSlot.ItemCode,
                    selectedQuickItem.QuickHpRestore,
                    selectedQuickItem.QuickMpRestore,
                    targets,
                    token);
                if (!consumeResult.Success)
                {
                    _log($"{channel}:{remote} Dungeon quick-item use rejected by persistence: room={session.DungeonRoomId} character={session.Character.Id} slot={requestedSlot} item={selectedQuickSlot.ItemCode}");
                    return BuildNativeFrame(
                        frame,
                        0xCF94,
                        BuildDungeonQuickItemUsePayload(
                            GetSceneEntityId(session.Character),
                            requestedSlot,
                            selectedQuickSlot.ItemCode,
                            0,
                            0),
                        session);
                }

                lock (_dungeonRoomGate)
                {
                    foreach (var targetResult in consumeResult.Targets)
                    {
                        var member = affectedMembers.FirstOrDefault(candidate =>
                            candidate.Character?.Id == targetResult.CharacterId);
                        if (member?.Character is null)
                            continue;
                        member.Character.CurrentHp = targetResult.CurrentHp;
                        member.Character.CurrentMp = targetResult.CurrentMp;
                    }

                    var sourceItem = session.Character.Items.FirstOrDefault(item =>
                        item.ItemCode == selectedQuickSlot.ItemCode);
                    if (sourceItem is not null)
                    {
                        if (consumeResult.RemainingQuantity == 0)
                            session.Character.Items.Remove(sourceItem);
                        else
                            sourceItem.Quantity = consumeResult.RemainingQuantity;
                    }
                    if (consumeResult.RemainingQuantity == 0)
                        session.Character.QuickSlots.RemoveAll(slot => slot.ItemCode == selectedQuickSlot.ItemCode);
                }

                var responseFrames = new List<byte[]>(consumeResult.Targets.Count);
                foreach (var targetResult in consumeResult.Targets)
                {
                    var member = affectedMembers.First(member =>
                        member.Character?.Id == targetResult.CharacterId);
                    var quickItemPayload = BuildDungeonQuickItemUsePayload(
                        GetSceneEntityId(member.Character!),
                        requestedSlot,
                        selectedQuickSlot.ItemCode,
                        targetResult.HpRestored,
                        targetResult.MpRestored);
                    QueueDungeonBroadcast(
                        session,
                        0xCF94,
                        quickItemPayload,
                        false,
                        "dungeon quick-item use");
                    responseFrames.Add(BuildNativeFrame(frame, 0xCF94, quickItemPayload, session));
                }
                _log($"{channel}:{remote} Dungeon quick-item consumed: room={session.DungeonRoomId} character={session.Character.Id} slot={requestedSlot} item={selectedQuickSlot.ItemCode} team={selectedQuickItem.QuickAffectsTeam} targets={consumeResult.Targets.Count} remaining={consumeResult.RemainingQuantity}");
                return CombineNativeFrames(responseFrames.ToArray());
            }

            case 0xCF9B: // REQ_USE_SKILLSLOT -> ANS_USE_SKILLSLOT
            {
                // Retail sends the selected slot (0=Z, 1=X), then expects the
                // response to carry the resolved skill code and grade.
                var requestedSlot = payload.Length == DungeonSkillUseRequestPayloadLength
                    ? BinaryPrimitives.ReadUInt32LittleEndian(payload)
                    : uint.MaxValue;
                var skillRoom = GetDungeonRoom(session);
                var skillBattle = skillRoom?.Battle;
                var activeSlots = session.Character is not null && requestedSlot <= 1
                    ? await GetEffectiveDungeonSkillSlotsAsync(session, token)
                    : default;
                var requestedSkillCode = requestedSlot switch
                {
                    0 => activeSlots.Skill0,
                    1 => activeSlots.Skill1,
                    _ => 0u
                };
                var grade = requestedSlot switch
                {
                    0 => activeSlots.Grade0,
                    1 => activeSlots.Grade1,
                    _ => (byte)0
                };
                if (!session.OnlineTracked
                    || session.Character is null
                    || payload.Length != DungeonSkillUseRequestPayloadLength
                    || requestedSlot > 1
                    || skillRoom is null
                    || skillBattle is null
                    || skillBattle.State != DungeonBattleState.Active)
                {
                    _log($"{channel}:{remote} Dungeon skill use rejected: online={session.OnlineTracked} room={session.DungeonRoomId} battleState={skillRoom?.Battle.State.ToString() ?? "none"} payload={payload.Length} slot={requestedSlot} skill={requestedSkillCode}");
                    return BuildNativeFrame(frame, 0xCF9C,
                        BuildDungeonSkillUseResultPayload(
                            session.Character is null ? (ushort)0 : GetSceneEntityId(session.Character),
                            1,
                            grade,
                            requestedSkillCode),
                        session);
                }

                var result = (byte)1;
                ushort manaCost = 0;
                ushort attackValue = 0;
                ushort activeFrames = 0;
                ushort cooldownFrames = 0;
                if (requestedSkillCode != 0
                    && grade is >= 1 and <= 5
                    && SkillCatalog.TryGet(requestedSkillCode, out var skill))
                {
                    manaCost = skill.GetManaCost(grade);
                    attackValue = skill.GetAttackValue(grade);
                    activeFrames = skill.GetActiveFrames(grade);
                    cooldownFrames = skill.GetCooldownFrames(grade);
                    var previousMp = session.Character.CurrentMp;
                    DungeonActiveSkillState? previousActiveSkill = null;
                    DungeonActiveSkillState? reservedActiveSkill = null;
                    long previousCooldownUntil = 0;
                    long reservedCooldownUntil = 0;
                    var hadPreviousCooldown = false;
                    var manaReserved = false;
                    lock (_dungeonRoomGate)
                    {
                        var now = Environment.TickCount64;
                        var cooldownKey = (session.Character.Id, requestedSkillCode);
                        hadPreviousCooldown = skillBattle.SkillCooldowns.TryGetValue(
                            cooldownKey,
                            out previousCooldownUntil);
                        if (manaCost != ushort.MaxValue
                            && attackValue > 0
                            && activeFrames > 0
                            && cooldownFrames != ushort.MaxValue
                            && ReferenceEquals(skillRoom.Battle, skillBattle)
                            && skillBattle.State == DungeonBattleState.Active
                            && skillRoom.Members.ContainsKey(session.SessionId)
                            && !skillBattle.DeadCharacters.Contains(session.Character.Id)
                            && (!hadPreviousCooldown || previousCooldownUntil <= now)
                            && session.Character.CurrentMp >= manaCost)
                        {
                            previousMp = session.Character.CurrentMp;
                            skillBattle.ActiveSkills.TryGetValue(
                                session.Character.Id,
                                out previousActiveSkill);
                            reservedActiveSkill = new DungeonActiveSkillState
                            {
                                SkillCode = requestedSkillCode,
                                Grade = grade,
                                AttackValue = attackValue,
                                ActiveFrames = activeFrames,
                                CreatesIndependentAttack = skill.CreatesIndependentAttack,
                                ActiveUntilTick = checked(now + DungeonFramesToMilliseconds(activeFrames))
                            };
                            session.Character.CurrentMp -= manaCost;
                            skillBattle.ActiveSkills[session.Character.Id] = reservedActiveSkill;
                            reservedCooldownUntil = checked(
                                now + DungeonFramesToMilliseconds(cooldownFrames));
                            skillBattle.SkillCooldowns[cooldownKey] = reservedCooldownUntil;
                            manaReserved = true;
                        }
                    }
                    if (manaReserved)
                    {
                        var saved = await _database.SaveCharacterRuntimeStateAsync(
                            session.AccountId,
                            session.Character.Id,
                            session.SessionId,
                            CreateRuntimeState(session.Character, session.ChannelId),
                            token);
                        if (saved)
                            result = 0;
                        else
                        {
                            lock (_dungeonRoomGate)
                            {
                                if (session.Character.CurrentMp == previousMp - manaCost)
                                    session.Character.CurrentMp = previousMp;
                                if (reservedActiveSkill is not null
                                    && skillBattle.ActiveSkills.TryGetValue(
                                        session.Character.Id,
                                        out var currentActiveSkill)
                                    && ReferenceEquals(currentActiveSkill, reservedActiveSkill))
                                {
                                    if (previousActiveSkill is null)
                                        skillBattle.ActiveSkills.Remove(session.Character.Id);
                                    else
                                        skillBattle.ActiveSkills[session.Character.Id] = previousActiveSkill;
                                }
                                var cooldownKey = (session.Character.Id, requestedSkillCode);
                                if (skillBattle.SkillCooldowns.TryGetValue(
                                        cooldownKey,
                                        out var currentCooldownUntil)
                                    && currentCooldownUntil == reservedCooldownUntil)
                                {
                                    if (hadPreviousCooldown)
                                        skillBattle.SkillCooldowns[cooldownKey] = previousCooldownUntil;
                                    else
                                        skillBattle.SkillCooldowns.Remove(cooldownKey);
                                }
                            }
                        }
                    }
                }

                var skillResultPayload = BuildDungeonSkillUseResultPayload(
                    GetSceneEntityId(session.Character),
                    result,
                    grade,
                    requestedSkillCode);
                if (result == 0)
                    QueueDungeonBroadcast(session, 0xCF9C, skillResultPayload, false, "dungeon skill use");
                _log($"{channel}:{remote} Dungeon skill use: room={session.DungeonRoomId} character={session.Character.Id} slot={requestedSlot} skill={requestedSkillCode} grade={grade} result={(result == 0 ? "success" : "failure")} mpCost={manaCost} attack={attackValue} activeFrames={activeFrames} cooldownFrames={cooldownFrames} mp={session.Character.CurrentMp}/{session.Character.MaxMp}");
                return BuildNativeFrame(frame, 0xCF9C, skillResultPayload, session);
            }

            case MentorProtocol.AdvertiseRequestOpcode:
            case MentorProtocol.StopAdvertisingRequestOpcode:
            {
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未建立世界会话的家教广告请求");
                    return null;
                }
                if (payload.Length != MentorProtocol.AdvertisementRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 家教广告请求长度无效：opcode=0x{opcode:X4} expected=0 actual={payload.Length}；未响应");
                    return null;
                }

                var enabled = opcode == MentorProtocol.AdvertiseRequestOpcode;
                await _database.SetMentorAdvertisingAsync(session.Character.Id, enabled, token);
                if (enabled)
                    _mentorAdvertisingCharacters[session.Character.Id] = 0;
                else
                    _mentorAdvertisingCharacters.TryRemove(session.Character.Id, out _);
                MentorStateChanged?.Invoke();

                var responseOpcode = enabled
                    ? MentorProtocol.AdvertiseResponseOpcode
                    : MentorProtocol.StopAdvertisingResponseOpcode;
                _log($"{channel}:{remote} 家教广告{(enabled ? "发布" : "停止")}成功：character={session.Character.Name} request=0x{opcode:X4} response=0x{responseOpcode:X4} result=0");
                return BuildNativeFrame(
                    frame,
                    responseOpcode,
                    MentorProtocol.BuildAdvertisementResult(0),
                    session);
            }

            case MentorProtocol.StudentRequestOpcode:
            case MentorProtocol.TeacherRequestOpcode:
            {
                if (opcode is CoupleProtocol.RingRequestOpcode or CoupleProtocol.SeparationRequestOpcode)
                    return await HandleCoupleRequestAsync(frame, opcode, payload, session, channel, remote, token);
                if (!session.OnlineTracked
                    || session.Character is null
                    || !_activeWorldSessions.TryGetValue(session.SessionId, out var requester))
                    return null;
                if (payload.Length != MentorProtocol.GetExpectedPayloadLength(opcode)
                    || !MentorProtocol.TryReadPeerName(payload, out var requestedPeerName))
                {
                    _log($"{channel}:{remote} 师生请求字段无效：opcode=0x{opcode:X4} expected={MentorProtocol.GetExpectedPayloadLength(opcode)} actual={payload.Length}；未响应");
                    return null;
                }

                var responder = FindMentorRequestTarget(
                    session,
                    opcode,
                    requestedPeerName,
                    MentorProtocol.ReadRequestedPeerUid(payload));
                if (responder?.Session.Character is null)
                {
                    _log($"{channel}:{remote} 师生请求目标不可用：opcode=0x{opcode:X4} requester={session.Character.Name} target={requestedPeerName}；返回官方超时状态 2");
                    return BuildNativeFrame(
                        frame,
                        MentorProtocol.GetResponseOpcode(opcode),
                        MentorProtocol.BuildUnavailableResponse(opcode, payload, requestedPeerName),
                        session);
                }

                var responseOpcode = MentorProtocol.GetResponseOpcode(opcode);
                var pendingKey = new MentorPendingKey(
                    responder.SessionId,
                    responseOpcode,
                    session.Character.Name);
                lock (_mentorGate)
                {
                    if (_mentorPendingRequests.ContainsKey(pendingKey))
                    {
                        _log($"{channel}:{remote} 师生请求重复：opcode=0x{opcode:X4} requester={session.Character.Name} target={responder.CharacterName}；返回官方超时状态 2");
                        return BuildNativeFrame(
                            frame,
                            responseOpcode,
                            MentorProtocol.BuildUnavailableResponse(opcode, payload, requestedPeerName),
                            session);
                    }
                }

                var interactionId = await _database.RecordMentorInteractionAsync(
                    opcode,
                    session.Character.Id,
                    responder.CharacterId,
                    MentorProtocol.ReadLessonCode(payload),
                    MentorProtocol.ReadRequestedPeerUid(payload),
                    token);
                var pending = new MentorPendingRequest(
                    interactionId,
                    opcode,
                    requester,
                    responder,
                    payload.ToArray(),
                    DateTime.UtcNow);
                var pendingAdded = false;
                lock (_mentorGate)
                {
                    pendingAdded = _activeWorldSessions.TryGetValue(responder.SessionId, out var activeResponder)
                                   && activeResponder.AccountId == responder.AccountId
                                   && activeResponder.Session.OnlineTracked
                                   && _mentorPendingRequests.TryAdd(pendingKey, pending);
                }
                if (!pendingAdded)
                {
                    await _database.CompleteMentorInteractionAsync(
                        interactionId,
                        MentorProtocol.TimedOut,
                        token);
                    MentorStateChanged?.Invoke();
                    _log($"{channel}:{remote} 师生请求目标在转发前离线：interaction={interactionId} target={responder.CharacterName}；返回官方超时状态 2");
                    return BuildNativeFrame(
                        frame,
                        responseOpcode,
                        MentorProtocol.BuildUnavailableResponse(opcode, payload, requestedPeerName),
                        session);
                }

                session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    responder,
                    opcode,
                    MentorProtocol.BuildRequestRelay(opcode, payload, session.Character),
                    $"mentor request interaction {interactionId}"));
                MentorStateChanged?.Invoke();
                _log($"{channel}:{remote} 师生请求已定向转发：interaction={interactionId} opcode=0x{opcode:X4} requester={session.Character.Name} target={responder.CharacterName} lesson={MentorProtocol.ReadLessonCode(payload)}");
                return null;
            }

            case MentorProtocol.StudentResponseOpcode:
            case MentorProtocol.TeacherResponseOpcode:
            {
                if (opcode is CoupleProtocol.RingResponseOpcode or CoupleProtocol.SeparationResponseOpcode)
                    return await HandleCoupleResponseAsync(frame, opcode, payload, session, channel, remote, token);
                if (!session.OnlineTracked
                    || session.Character is null
                    || payload.Length != MentorProtocol.GetExpectedPayloadLength(opcode)
                    || !MentorProtocol.TryReadPeerName(payload, out var requesterName))
                {
                    _log($"{channel}:{remote} 师生回答字段无效：opcode=0x{opcode:X4} expected={MentorProtocol.GetExpectedPayloadLength(opcode)} actual={payload.Length}；未转发");
                    return null;
                }

                var status = MentorProtocol.ReadResponseStatus(opcode, payload);
                if (!MentorProtocol.IsOfficialResponseStatus(status))
                {
                    _log($"{channel}:{remote} 甯堢敓鍥炵瓟鐘舵€佷笉鏄畼鏂圭姸鎬佸€硷細opcode=0x{opcode:X4} status={status}锛涙湭杞彂");
                    return null;
                }

                var pendingKey = new MentorPendingKey(session.SessionId, opcode, requesterName);
                MentorPendingRequest? pending = null;
                lock (_mentorGate)
                {
                    if (_mentorPendingRequests.TryGetValue(pendingKey, out var candidate)
                        && candidate.Responder.SessionId == session.SessionId
                        && MentorProtocol.ResponseMatchesRequest(opcode, candidate.RequestPayload, payload))
                    {
                        pending = candidate;
                        _mentorPendingRequests.Remove(pendingKey);
                    }
                }
                if (pending is null)
                {
                    _log($"{channel}:{remote} 师生回答与原请求不匹配或请求已失效：opcode=0x{opcode:X4} responder={session.Character.Name} requester={requesterName}；未转发");
                    return null;
                }

                await _database.CompleteMentorInteractionAsync(pending.InteractionId, status, token);
                if (_activeWorldSessions.TryGetValue(pending.Requester.SessionId, out var activeRequester)
                    && activeRequester.AccountId == pending.Requester.AccountId)
                {
                    session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                        activeRequester,
                        opcode,
                        MentorProtocol.BuildResponseRelay(opcode, payload, session.Character),
                        $"mentor response interaction {pending.InteractionId}"));
                }
                MentorStateChanged?.Invoke();
                _log($"{channel}:{remote} 甯堢敓鍥炵瓟宸叉敹鍙ｏ細interaction={pending.InteractionId} opcode=0x{opcode:X4} responder={session.Character.Name} requester={pending.Requester.CharacterName} status={status}");
                return null;
            }

            case MentorProtocol.CreateSchoolingRoomRequestOpcode:
                if (!session.OnlineTracked || session.Character is null)
                {
                    _log($"{channel}:{remote} 拒绝未建立世界会话的师生房间创建请求");
                    return null;
                }
                if (payload.Length != 0)
                {
                    _log($"{channel}:{remote} 师生房间创建请求长度无效：期望 0，实际 {payload.Length}；未响应");
                    return null;
                }

                // The C579 consumer reads only the ushort result at frame+8.
                // Zero enters the retail CSchoolingListBox room flow.
                _log($"{channel}:{remote} 师生房间创建成功：characterId={session.Character.Id}");
                return BuildNativeFrame(
                    frame,
                    MentorProtocol.CreateSchoolingRoomResponseOpcode,
                    BuildMakeDdakgiRoomPayload(),
                    session);

            case MentorProtocol.ListRequestOpcode:
                if (!session.OnlineTracked || session.Character is null)
                    return null;
                if (payload.Length != MentorProtocol.ListRequestPayloadLength)
                {
                    _log($"{channel}:{remote} 师生列表请求长度无效：期望 4，实际 {payload.Length}；未响应");
                    return null;
                }

                var mentorListPage = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                var advertisingCharacters = _activeWorldSessions.Values
                    .Where(item => item.ChannelId == session.ChannelId
                                   && item.Session.OnlineTracked
                                   && item.Session.Character is not null
                                   && _mentorAdvertisingCharacters.ContainsKey(item.CharacterId))
                    .GroupBy(item => item.CharacterId)
                    .Select(group => group.First().Session.Character!)
                    .OrderBy(character => character.Id)
                    .Take(MentorProtocol.ListPageSize * (MentorProtocol.MaximumListPage + 1))
                    .ToArray();
                var mentorListPayload = MentorProtocol.BuildListResult(
                    mentorListPage,
                    advertisingCharacters);
                _log($"{channel}:{remote} 返回师生招生列表：character={session.Character.Name} page={mentorListPage} count={BinaryPrimitives.ReadUInt32LittleEndian(mentorListPayload.AsSpan(4, 4))} total={advertisingCharacters.Length}");
                return BuildNativeFrame(
                    frame,
                    MentorProtocol.ListResponseOpcode,
                    mentorListPayload,
                    session);

            case 0xEB29: // nProtect GameGuard auth data -> success result
                if (payload.Length != 16)
                {
                    _log($"{channel}:{remote} GameGuard 验证包长度无效：期望 16，实际 {payload.Length}；未响应");
                    return null;
                }
                _log($"{channel}:{remote} GameGuard 验证握手完成；返回兼容成功结果且不写入角色存档");
                return BuildNativeFrame(frame, 0xEB2A, new byte[4], session);

            case 0xEB8F: // AntiBot::IClientSink::SendDataToSvr callback
            {
                if (payload.Length != AntiBotSmallPayloadLength)
                {
                    _log($"{channel}:{remote} AntiBot 上报包长度无效：期望 {AntiBotSmallPayloadLength}，实际 {payload.Length}；未响应且未修改存档");
                    return null;
                }

                var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                if (dataLength > AntiBotSmallDataCapacity)
                {
                    _log($"{channel}:{remote} AntiBot 上报声明长度无效：最大 {AntiBotSmallDataCapacity}，实际 {dataLength}；未响应且未修改存档");
                    return null;
                }

                _log($"{channel}:{remote} AntiBot 单向上报有效数据 {dataLength} 字节、固定填充 {AntiBotSmallDataCapacity - dataLength} 字节；无需响应且不写入账号或角色存档");
                return null;
            }

            case 0xC4AF: // REQ_ACCEPT_TRADE_HANS_DDAKGI -> ACCEPT_TRADE_HANS_DDAKGI
            {
                if (!session.OnlineTracked || session.Character is null
                    || payload.Length != TradeInviteRequestPayloadLength)
                    return null;

                var targetEntityUid = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                var invitee = FindVisibleScenePeer(session, targetEntityUid);
                if (invitee is null
                    || invitee.Session.Character is null
                    || !TryCreateTradeInvitation(session, invitee.Session))
                {
                    _log($"{channel}:{remote} trade invitation rejected: inviter={session.Character.Id} target={targetEntityUid}");
                    return BuildNativeFrame(
                        frame,
                        0xC4B2,
                        BuildTradeInvitationFailurePayload(targetEntityUid),
                        session);
                }

                session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    invitee,
                    0xC4B0,
                    BuildTradeInvitationPayload(session.Character),
                    "trade-room invitation"));
                _log($"{channel}:{remote} trade invitation queued: inviter={session.Character.Id} invitee={invitee.CharacterId}");
                return null;
            }

            case 0xC4E0: // SEND_PARTY_INVITATION -> RECV_PARTY_INVITATION
            {
                if (!session.OnlineTracked || session.Character is null
                    || payload.Length != PartyInvitationPayloadLength)
                {
                    _log($"{channel}:{remote} party invitation rejected: expected={PartyInvitationPayloadLength} actual={payload.Length}");
                    return null;
                }

                var inviterEntityUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(16, 2));
                var targetEntityUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(22, 2));
                var expectedInviterEntityUid = GetSceneEntityId(session.Character);
                if (inviterEntityUid != expectedInviterEntityUid || targetEntityUid == expectedInviterEntityUid)
                {
                    _log($"{channel}:{remote} party invitation identity rejected: character={session.Character.Id} inviter={inviterEntityUid} expected={expectedInviterEntityUid} target={targetEntityUid}");
                    return null;
                }

                var invitee = FindPartyInvitationTarget(session, targetEntityUid);
                if (invitee is null)
                {
                    _log($"{channel}:{remote} party invitation target is not visible: inviter={session.Character.Id} target={targetEntityUid}");
                    return null;
                }

                if (!TryCreatePartyInvitation(session, invitee.Session, payload.AsSpan(18, 4)))
                {
                    _log($"{channel}:{remote} party invitation rejected by party state: inviter={session.Character.Id} target={targetEntityUid}");
                    return null;
                }

                session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    invitee,
                    0xC4E0,
                    BuildPartyInvitationPayload(payload, session.Character),
                    "party invitation"));
                _log($"{channel}:{remote} party invitation forwarded: inviter={session.Character.Id} invitee={invitee.CharacterId}");
                return null;
            }

            case 0xC4E1: // SEND_PARTY_INVITATION_RESULT -> PARTY_UNION_RESULT
            {
                if (!session.OnlineTracked || session.Character is null
                    || payload.Length != PartyAgreementPayloadLength)
                {
                    _log($"{channel}:{remote} party agreement rejected: expected={PartyAgreementPayloadLength} actual={payload.Length}");
                    return null;
                }

                var agreementCode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var inviterEntityUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                if (agreementCode is not (PartyAgreementRefused
                    or PartyAgreementAccepted
                    or PartyAgreementUnavailable)
                    || !TryResolvePartyAgreement(
                        session,
                        inviterEntityUid,
                        agreementCode,
                        out var resultCode,
                        out var unionPayload,
                        out var recipients))
                {
                    _log($"{channel}:{remote} invalid or stale party agreement: invitee={session.Character.Id} inviter={inviterEntityUid} code={agreementCode}");
                    return null;
                }

                foreach (var recipient in recipients.Where(item => item.SessionId != session.SessionId))
                {
                    session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                        recipient,
                        0xC4E2,
                        unionPayload.ToArray(),
                        "party union result"));
                }

                _log($"{channel}:{remote} party agreement resolved: invitee={session.Character.Id} inviter={inviterEntityUid} requested={agreementCode} result={resultCode} party={session.PartyId}");
                return BuildNativeFrame(frame, 0xC4E2, unionPayload, session);
            }

            case 0xC4E3: // REQ_CHANGE_PARTY_OWNER -> ANS_CHANGE_PARTY_OWNER
            {
                if (!session.OnlineTracked || session.Character is null
                    || payload.Length != PartyOwnerChangeRequestPayloadLength)
                    return null;

                var requestedOwnerUid = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                WorldPresence[] partyRecipients = [];
                var changed = requestedOwnerUid <= ushort.MaxValue
                    && TryChangePartyOwner(
                        session,
                        checked((ushort)requestedOwnerUid),
                        out partyRecipients);
                var ownerChangePayload = BuildPartyOwnerChangePayload(
                    changed,
                    changed ? checked((ushort)requestedOwnerUid) : (ushort)0);
                if (changed)
                {
                    foreach (var recipient in partyRecipients.Where(item => item.SessionId != session.SessionId))
                    {
                        session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                            recipient,
                            0xC4E4,
                            ownerChangePayload.ToArray(),
                            "party owner changed"));
                    }
                }
                _log($"{channel}:{remote} party owner change: requester={session.Character.Id} target={requestedOwnerUid} result={(changed ? 1 : 0)}");
                return BuildNativeFrame(frame, 0xC4E4, ownerChangePayload, session);
            }

            case 0xC4E5: // REQ_WANT_EXIT_PARTY_MEMBER -> ANS_WANT_EXIT_PARTY_MEMBER
            {
                if (!session.OnlineTracked || session.Character is null
                    || payload.Length != PartyMemberRemoveRequestPayloadLength)
                    return null;

                var targetUidValue = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                byte partyState = 0;
                List<PartyLeaveNotification> removalNotifications = [];
                var removed = targetUidValue <= ushort.MaxValue
                    && TryRemovePartyMember(
                        session,
                        checked((ushort)targetUidValue),
                        out partyState,
                        out removalNotifications);
                if (removed)
                {
                    foreach (var notification in removalNotifications)
                    {
                        session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                            notification.Target,
                            0xC4E8,
                            notification.Payload,
                            "party member removed"));
                    }
                }
                _log($"{channel}:{remote} party member removal: owner={session.Character.Id} target={targetUidValue} result={(removed ? 1 : 0)} state={partyState}");
                return BuildNativeFrame(
                    frame,
                    0xC4E6,
                    BuildPartyMemberRemovePayload(
                        removed,
                        removed ? partyState : (byte)0,
                        targetUidValue <= ushort.MaxValue ? checked((ushort)targetUidValue) : (ushort)0),
                    session);
            }

            case 0xC4E7: // REQ_PARTY_LEAVE -> ANS_PARTY_LEAVE
            {
                if (!session.OnlineTracked || session.Character is null
                    || payload.Length != PartyLeaveRequestPayloadLength)
                {
                    _log($"{channel}:{remote} party leave rejected: expected={PartyLeaveRequestPayloadLength} actual={payload.Length}");
                    return null;
                }

                if (!TryLeaveParty(session, 10, out var selfPayload, out var notifications))
                {
                    _log($"{channel}:{remote} party leave ignored because character is not in a party: character={session.Character.Id}");
                    return null;
                }

                foreach (var notification in notifications)
                {
                    session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                        notification.Target,
                        0xC4E8,
                        notification.Payload,
                        "party member leave"));
                }
                _log($"{channel}:{remote} party member left: character={session.Character.Id} remainingNotifications={notifications.Count}");
                return BuildNativeFrame(frame, 0xC4E8, selfPayload, session);
            }

            case 0xC4B1: // AGREE_TRADE_HANS_DDAKGI
            {
                if (!session.OnlineTracked || session.Character is null
                    || payload.Length != TradeAgreementPayloadLength)
                    return null;

                var inviterEntityUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                var agreementCode = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                if (agreementCode is not (TradeAgreementAccepted
                    or TradeAgreementRefused
                    or TradeAgreementNotReady)
                    || !TryDecodeFixedGbkString(payload.AsSpan(4, 16), true, out _)
                    || !TryResolveTradeAgreement(
                        session,
                        inviterEntityUid,
                        agreementCode == TradeAgreementAccepted,
                        out var inviter))
                {
                    _log($"{channel}:{remote} invalid or stale trade agreement: invitee={session.Character.Id} inviter={inviterEntityUid} code={agreementCode}");
                    return null;
                }

                session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    inviter,
                    0xC4B1,
                    BuildTradeAgreementPayload(session.Character, agreementCode),
                    "trade-room agreement"));
                _log($"{channel}:{remote} trade agreement forwarded: invitee={session.Character.Id} inviter={inviter.CharacterId} code={agreementCode}");
                return null;
            }

            case 0xC4B3: // REQ_CREATE_TRADE_ROOM -> ANS_CREATE_TRADE_ROOM
            {
                if (!session.OnlineTracked || session.Character is null
                    || payload.Length != TradeCreateRequestPayloadLength)
                    return null;

                var inviteeEntityUid = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var positionX = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
                var positionY = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                if (!TryCreateTradeRoom(session, inviteeEntityUid, out var tradeRoom, out var invitee))
                {
                    _log($"{channel}:{remote} trade-room creation rejected: inviter={session.Character.Id} invitee={inviteeEntityUid}");
                    return BuildNativeFrame(
                        frame,
                        0xC4B4,
                        BuildTradeRoomCreateResultPayload(false, 0),
                        session);
                }

                LeaveApartmentScene(session, "trade-room create");
                LeaveVillageShopScene(session, "trade-room create");
                LeaveTownScene(session, "trade-room create");
                session.LastReportedPositionX = positionX;
                session.LastReportedPositionY = positionY;
                session.Character.PositionX = positionX;
                session.Character.PositionY = positionY;
                session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    invitee,
                    0xC4B5,
                    BuildTradeRoomInvitationPayload(tradeRoom.Id),
                    "trade-room id invitation"));
                _log($"{channel}:{remote} trade room created: room={tradeRoom.Id} inviter={session.Character.Id} invitee={invitee.CharacterId}");
                return BuildNativeFrame(
                    frame,
                    0xC4B4,
                    BuildTradeRoomCreateResultPayload(true, tradeRoom.Id),
                    session);
            }

            case 0xC4B7: // REQ_ENTER_TRADE_ROOM -> ANS_ENTER_TRADE_ROOM
            {
                if (!session.OnlineTracked || session.Character is null
                    || payload.Length != TradeEnterRequestPayloadLength)
                    return null;

                var roomId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                var positionX = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
                var positionY = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
                if (roomId > ushort.MaxValue || !TryEnterTradeRoom(session, checked((int)roomId)))
                {
                    _log($"{channel}:{remote} trade-room entry rejected: character={session.Character.Id} room={roomId}");
                    return null;
                }

                LeaveApartmentScene(session, "trade-room enter");
                LeaveVillageShopScene(session, "trade-room enter");
                LeaveTownScene(session, "trade-room enter");
                session.LastReportedPositionX = positionX;
                session.LastReportedPositionY = positionY;
                session.Character.PositionX = positionX;
                session.Character.PositionY = positionY;
                _log($"{channel}:{remote} trade room entered: room={roomId} character={session.Character.Id}");
                return BuildNativeFrame(
                    frame,
                    0xC4B6,
                    BuildTradeRoomEnterResultPayload(checked((int)roomId)),
                    session);
            }

            case 0xC4B8: // REQ_TRADE_SETTING_FINISH -> ANS_TRADE_SETTING_FINISH
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != 0)
                    return null;
                var peer = FindTradeRoomPeer(session);
                if (peer?.Character is null)
                {
                    _log($"{channel}:{remote} trade peer-info request has no peer: room={session.TradeRoomId} character={session.Character.Id}");
                    return null;
                }

                _log($"{channel}:{remote} trade peer info returned: room={session.TradeRoomId} requester={session.Character.Id} peer={peer.Character.Id}");
                return BuildNativeFrame(
                    frame,
                    0xC4B9,
                    BuildTradePeerInfoPayload(peer.Character),
                    session);
            }

            case 0xC4BA: // REQ_PUT_TRADE_HANS_DDAKGI -> ANS_PUT_TRADE_HANS_DDAKGI
            {
                if (!session.OnlineTracked || session.Character is null
                    || payload.Length != TradeOfferRequestPayloadLength)
                    return null;

                var offerResult = await TryApplyTradeOfferAsync(session, payload, token);
                if (offerResult is null)
                {
                    _log($"{channel}:{remote} trade offer rejected: room={session.TradeRoomId} character={session.Character.Id}");
                    return null;
                }

                _log($"{channel}:{remote} trade offer updated: room={session.TradeRoomId} character={session.Character.Id}");
                return BuildNativeFrame(frame, 0xC4BB, offerResult, session);
            }

            case 0xC4BC: // READY_TRADE_HANS_DDAKGI -> READY_TRADE_HANS_DDAKGI
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != 0
                    || !TryReadyTrade(session, out var readyPeer, out var readyPayload))
                    return null;

                session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    readyPeer,
                    0xC4BD,
                    readyPayload,
                    "trade offer ready"));
                _log($"{channel}:{remote} trade offer ready: room={session.TradeRoomId} character={session.Character.Id}");
                return null;
            }

            case 0xC4BE: // CANCEL_TRADE_HANS_DDAKGI
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != 0
                    || !TryCancelTrade(session, out var cancelPeer))
                    return null;

                session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    cancelPeer,
                    0xC4BE,
                    [],
                    "trade confirmation cancelled"));
                _log($"{channel}:{remote} trade confirmation cancelled: room={session.TradeRoomId} character={session.Character.Id}");
                return null;
            }

            case 0xC4BF: // REQ_TRADE_HANS_DDAKGI
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != 0
                    || !TryRequestTradeSettlement(session, out var finalPeer, out var settlement))
                    return null;

                session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    finalPeer,
                    0xC4BF,
                    [],
                    "trade final confirmation"));
                if (settlement is null)
                {
                    _log($"{channel}:{remote} first trade confirmation recorded: room={session.TradeRoomId} character={session.Character.Id}");
                    return null;
                }

                var firstCards = BuildTradeCardQuantities(settlement.FirstOffer);
                var secondCards = BuildTradeCardQuantities(settlement.SecondOffer);
                (uint ResultCode, long FirstHans, long SecondHans) result;
                var settlementResultCode = TradeResultFailed;
                try
                {
                    result = await _database.CompletePlayerTradeAsync(
                        settlement.First.AccountId,
                        settlement.First.Character!.Id,
                        settlement.First.SessionId,
                        settlement.FirstOffer.Hans,
                        firstCards,
                        settlement.Second.AccountId,
                        settlement.Second.Character!.Id,
                        settlement.Second.SessionId,
                        settlement.SecondOffer.Hans,
                        secondCards,
                        token);
                    settlementResultCode = result.ResultCode;
                }
                finally
                {
                    // The protocol confirmation lock must not survive a cancelled or failed DB call.
                    CompleteTradeSettlementState(settlement, settlementResultCode);
                }

                if (result.ResultCode == TradeResultSuccess)
                {
                    settlement.First.Character.Hans = result.FirstHans;
                    settlement.Second.Character.Hans = result.SecondHans;
                }

                var resultPayload = BuildTradeSettlementResultPayload(result.ResultCode);
                session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    finalPeer,
                    0xC4C0,
                    resultPayload.ToArray(),
                    "trade settlement result"));
                _log($"{channel}:{remote} trade settlement completed: room={settlement.RoomId} first={settlement.First.Character.Id} second={settlement.Second.Character.Id} result={result.ResultCode}");
                return BuildNativeFrame(frame, 0xC4C0, resultPayload, session);
            }

            case 0xCB21: // bidirectional scene movement/entity state
                if (!session.OnlineTracked || session.Character is null || payload.Length != 16)
                {
                    _log($"{channel}:{remote} 场景移动上报无效：online={session.OnlineTracked} payload={payload.Length}；未响应且未修改存档");
                    return null;
                }
                var movementPayload = BuildSceneMovementPayload(payload, session.Character);
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (!session.AuxiliaryGameSession || GetEntertainmentRoom(session) is null)
                            return null;
                        QueueEntertainmentBroadcast(
                            session,
                            0xCB21,
                            movementPayload,
                            false,
                            "entertainment movement/state");
                        return null;
                    }
                    if (!session.AuxiliaryGameSession || GetArenaRoom(session) is null)
                        return null;
                    QueueArenaBroadcast(session, 0xCB21, movementPayload, false, "arena movement/state");
                    return null;
                }
                var movementPositionX = BinaryPrimitives.ReadUInt16LittleEndian(movementPayload.AsSpan(8, 2));
                var movementPositionY = BinaryPrimitives.ReadUInt16LittleEndian(movementPayload.AsSpan(10, 2));
                if (TownPositionPolicy.IsPersistable(movementPositionX, movementPositionY))
                {
                    session.LastReportedPositionX = movementPositionX;
                    session.LastReportedPositionY = movementPositionY;
                    session.Character.PositionX = movementPositionX;
                    session.Character.PositionY = movementPositionY;
                }
                // FFFF/FFFF is an observed CB21 activity sentinel. Relay the
                // frame unchanged for the page-scoped client state machine, but
                // never let it replace the last legal persistent town position.
                QueueSceneBroadcast(session, 0xCB21, movementPayload, "scene movement/state");
                return null;

            case 0xCB22: // bidirectional MEDIATE_USER_EMOTION
                if (!session.OnlineTracked || session.Character is null || payload.Length != SceneEmotionPayloadLength)
                {
                    _log($"{channel}:{remote} scene emotion invalid: online={session.OnlineTracked} payload={payload.Length}; no response");
                    return null;
                }
                var emotionPayload = payload.ToArray();
                BinaryPrimitives.WriteUInt16LittleEndian(
                    emotionPayload.AsSpan(0, 2),
                    GetSceneEntityId(session.Character));
                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (!session.AuxiliaryGameSession || GetEntertainmentRoom(session) is null)
                            return null;
                        QueueEntertainmentBroadcast(
                            session,
                            0xCB22,
                            emotionPayload,
                            false,
                            "entertainment emotion");
                        return BuildNativeFrame(frame, 0xCB22, emotionPayload, session);
                    }
                    if (!session.AuxiliaryGameSession || GetArenaRoom(session) is null)
                        return null;
                    QueueArenaBroadcast(session, 0xCB22, emotionPayload, false, "arena emotion");
                    return BuildNativeFrame(frame, 0xCB22, emotionPayload, session);
                }
                QueueSceneBroadcast(session, 0xCB22, emotionPayload, "scene emotion");
                return BuildNativeFrame(frame, 0xCB22, emotionPayload, session);

            case 0xCB23: // bidirectional MEDIATE_USER_CHAT
            {
                if (!session.OnlineTracked || session.Character is null || payload.Length != SceneChatPayloadLength)
                {
                    _log($"{channel}:{remote} scene chat invalid: online={session.OnlineTracked} payload={payload.Length}; no response");
                    return null;
                }

                var chatType = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
                if (chatType is 4000 or 5000)
                {
                    if (!TryDecodeFixedGbkString(payload.AsSpan(36, 64), false, out var mikeText))
                    {
                        _log($"{channel}:{remote} mike text invalid: type={chatType}; no response");
                        return null;
                    }

                    var isGlobalMike = chatType == 5000;
                    var consumeResult = await _database.ConsumeMikeUseAsync(
                        session.AccountId,
                        session.Character.Id,
                        session.SessionId,
                        isGlobalMike,
                        token);
                    if (!consumeResult.Success)
                    {
                        _log($"{channel}:{remote} mike send rejected: type={chatType} error={consumeResult.Error}; no response");
                        return null;
                    }

                    var mikePayload = new byte[SceneChatPayloadLength];
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        mikePayload.AsSpan(0, 2),
                        checked((ushort)Math.Clamp(session.ChannelId - 1, 0, ushort.MaxValue)));
                    BinaryPrimitives.WriteUInt16LittleEndian(mikePayload.AsSpan(2, 2), chatType);
                    WriteFixedGbk(mikePayload.AsSpan(4, 32), session.Character.Name);
                    WriteFixedGbk(mikePayload.AsSpan(36, 64), mikeText);
                    if (isGlobalMike)
                        session.Character.MikeGlobalUseCount = consumeResult.RemainingUseCount;
                    else
                        session.Character.MikeChannelUseCount = consumeResult.RemainingUseCount;
                    QueueMikeBroadcast(session, 0xCB23, mikePayload, isGlobalMike);
                    _log($"{channel}:{remote} mike sent: type={chatType} sender={session.Character.Name} channel={session.ChannelId} remaining={consumeResult.RemainingUseCount} bytes={Encoding.GetEncoding(936).GetByteCount(mikeText)}");
                    return BuildNativeFrame(frame, 0xCB23, mikePayload, session);
                }

                if (chatType is not (1000 or 2000 or 3000)
                    || !TryDecodeFixedGbkString(payload.AsSpan(36, 64), false, out var chatText)
                    || !TryDecodeFixedGbkString(payload.AsSpan(20, 16), true, out var whisperTargetName))
                {
                    _log($"{channel}:{remote} scene chat fields invalid: type={chatType}; no response");
                    return null;
                }

                var chatPayload = new byte[SceneChatPayloadLength];
                BinaryPrimitives.WriteUInt16LittleEndian(
                    chatPayload.AsSpan(0, 2),
                    GetSceneEntityId(session.Character));
                BinaryPrimitives.WriteUInt16LittleEndian(chatPayload.AsSpan(2, 2), chatType);
                WriteFixedGbk(chatPayload.AsSpan(4, 16), session.Character.Name);
                WriteFixedGbk(chatPayload.AsSpan(20, 16), whisperTargetName);
                WriteFixedGbk(chatPayload.AsSpan(36, 64), chatText);

                if (string.Equals(channel, "ArenaAdapter", StringComparison.Ordinal))
                {
                    if (IsEntertainmentSession(channel, session))
                    {
                        if (!session.AuxiliaryGameSession || GetEntertainmentRoom(session) is null)
                            return null;
                        QueueEntertainmentBroadcast(
                            session,
                            0xCB23,
                            chatPayload,
                            false,
                            "entertainment room chat");
                        return BuildNativeFrame(frame, 0xCB23, chatPayload, session);
                    }
                    if (!session.AuxiliaryGameSession || GetArenaRoom(session) is null)
                        return null;
                    QueueArenaBroadcast(session, 0xCB23, chatPayload, false, "arena room chat");
                    _log($"{channel}:{remote} Arena room chat relayed: room={session.ArenaRoomId} type={chatType} sender={session.Character.Name}");
                    return BuildNativeFrame(frame, 0xCB23, chatPayload, session);
                }

                if (chatType == 2000)
                {
                    if (string.IsNullOrWhiteSpace(whisperTargetName))
                        return BuildNativeFrame(frame, 0xCB24, new byte[4], session);

                    if (TryQueueVillageBotWhisperReply(session, whisperTargetName, chatText))
                    {
                        _log($"{channel}:{remote} village bot whisper: sender={session.Character.Name} target={whisperTargetName}");
                        return BuildNativeFrame(frame, 0xCB23, chatPayload, session);
                    }

                    var whisperTarget = _activeWorldSessions.Values.FirstOrDefault(item =>
                        item.Session.OnlineTracked
                        && string.Equals(item.CharacterName, whisperTargetName, StringComparison.OrdinalIgnoreCase));
                    if (whisperTarget is null)
                    {
                        _log($"{channel}:{remote} whisper target not found: target={whisperTargetName}");
                        return BuildNativeFrame(frame, 0xCB24, new byte[4], session);
                    }

                    if (whisperTarget.SessionId != session.SessionId)
                    {
                        session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                            whisperTarget,
                            0xCB23,
                            chatPayload.ToArray(),
                            "whisper chat"));
                    }
                }
                else if (chatType == 3000)
                {
                    if (session.PartyId != 0)
                        QueuePartyBroadcast(session, 0xCB23, chatPayload, "party chat");
                    else if (session.DungeonRoomId != 0)
                        QueueDungeonBroadcast(session, 0xCB23, chatPayload, false, "dungeon room fallback chat");
                    else
                        _log($"{channel}:{remote} party chat has no active party model; echoed only to sender");
                }
                else
                {
                    QueueSceneBroadcast(session, 0xCB23, chatPayload, "scene all chat");
                    QueueVillageBotConversationReply(session, chatText);
                }

                _log($"{channel}:{remote} scene chat: type={chatType} sender={session.Character.Name} target={whisperTargetName} bytes={Encoding.GetEncoding(936).GetByteCount(chatText)}");
                return BuildNativeFrame(frame, 0xCB23, chatPayload, session);
            }

            case 0x2714:
            case 0x2718:
            case 0x271A:
            case 0x271C:
            case 0x2731: // legacy Tencent launcher login result
            case 0x2733:
            case 0xC352:
            case 0xC355:
            case 0xC379:
            case 0xC37B:
            case 0xC3AC:
            case 0xC3AE:
            case 0xC3AF:
            case 0xC38E:
            case 0xC390:
            case 0xC391:
            case 0xC393:
            case 0xC3CC:
            case 0xC3D0:
            case 0xC3E8:
            case 0xC3EA:
            case 0xC3EE:
            case 0xC3F4:
            case 0xC3FC:
            case 0xC40A:
            case 0xC410:
            case 0xC430:
            case 0xC434:
            case 0xC474:
            case 0xC46A:
            case 0xC46C:
            case 0xC46E:
            case 0xC40C:
            case 0xC399:
            case 0xC418:
            case 0xC41A:
            case 0xC41C:
            case 0xC438:
            case 0xC43A:
            case 0xC43C:
            case 0xC44C:
            case 0xC44E:
            case 0xC450:
            case 0xC452:
            case 0xC454:
            case 0xC476:
            case 0xC481:
            case 0xC588:
            case 0xC579:
            case 0xC57E:
            case 0xC594:
            case 0xC59C:
            case 0xC366:
            case 0xC368:
            case 0xC36A:
            case 0xC36B:
            case 0xC377:
            case 0xC4B0:
            case 0xC4B2:
            case 0xC4B4:
            case 0xC4B5:
            case 0xC4B6:
            case 0xC4B9:
            case 0xC4BB:
            case 0xC4BD:
            case 0xC4C0:
            case 0xC4C1:
            case 0xC555:
            case 0xC4E2:
            case 0xC4E4:
            case 0xC4E6:
            case 0xC4E8:
            case 0xC5AB:
            case 0xC5B1:
            case 0xCF0A:
            case 0xCF10:
            case 0xCF1E:
            case 0xCF6D:
            case 0xCF6F:
            case 0xCF71:
            case 0xCF74:
            case 0xCF76:
            case 0xCF7C:
            case 0xCF7E:
            case 0xCF80:
            case 0xCF96:
            case 0xCFD2:
            case 0xCFD4:
            case 0xCFD6:
            case 0xCFDA:
            case 0xCFEC:
            case 0xD00E:
            case 0xD010:
            case 0xCB24:
            case 0xEB28:
            case 0xEB2A:
            case 0xEB8C:
            case 0xEB8E:
            case 0xEB90:
            case 0xEB91:
                _log($"{channel}:{remote} 协议方向异常：opcode=0x{opcode:X4} 是 adapter 下行包，但从客户端收到 payload={payload.Length}；未响应且未修改存档");
                return null;

            default:
                _log($"{channel}:{remote} 未映射原生 opcode=0x{opcode:X4}，仅记录不回包");
                return null;
        }
    }

    private static bool IsSameTownPage(ConnectionSession source, ConnectionSession target)
        => source.SessionId != target.SessionId
           && source.OnlineTracked
           && target.OnlineTracked
           && source.TownSceneActive
           && target.TownSceneActive
           && source.ChannelId == target.ChannelId
           && source.TownId == target.TownId
           && source.TownPage == target.TownPage;

    private static bool IsSameApartmentRoom(ConnectionSession source, ConnectionSession target)
        => source.SessionId != target.SessionId
           && source.OnlineTracked
           && target.OnlineTracked
           && source.ApartmentOwnerCharacterId > 0
           && source.ChannelId == target.ChannelId
           && source.ApartmentOwnerCharacterId == target.ApartmentOwnerCharacterId;

    private static bool IsSameVillageShop(ConnectionSession source, ConnectionSession target)
        => source.SessionId != target.SessionId
           && source.OnlineTracked
           && target.OnlineTracked
           && source.VillageShopCode != 0
           && source.ChannelId == target.ChannelId
           && source.VillageShopCode == target.VillageShopCode;

    private static bool IsSameTradeRoom(ConnectionSession source, ConnectionSession target)
        => source.SessionId != target.SessionId
           && source.OnlineTracked
           && target.OnlineTracked
           && source.TradeRoomId > 0
           && source.ChannelId == target.ChannelId
           && source.TradeRoomId == target.TradeRoomId;

    private void QueueTownBroadcast(
        ConnectionSession source,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        string reason)
    {
        foreach (var target in _activeWorldSessions.Values
                     .Where(item => IsSameTownPage(source, item.Session))
                     .Where(item => item.Session.SessionId != source.SessionId))
        {
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                target,
                opcode,
                payload.ToArray(),
                reason));
        }
    }

    private void QueueApartmentBroadcast(
        ConnectionSession source,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        string reason)
    {
        foreach (var target in _activeWorldSessions.Values
                     .Where(item => IsSameApartmentRoom(source, item.Session)))
        {
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                target,
                opcode,
                payload.ToArray(),
                reason));
        }
    }

    private void QueueVillageShopBroadcast(
        ConnectionSession source,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        string reason)
    {
        foreach (var target in _activeWorldSessions.Values
                     .Where(item => IsSameVillageShop(source, item.Session)))
        {
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                target,
                opcode,
                payload.ToArray(),
                reason));
        }
    }

    private void QueueTradeRoomBroadcast(
        ConnectionSession source,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        string reason)
    {
        foreach (var target in _activeWorldSessions.Values
                     .Where(item => IsSameTradeRoom(source, item.Session)))
        {
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                target,
                opcode,
                payload.ToArray(),
                reason));
        }
    }

    private void QueuePartyBroadcast(
        ConnectionSession source,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        string reason)
    {
        WorldPresence[] recipients;
        lock (_partyGate)
        {
            if (!_parties.TryGetValue(source.PartyId, out var party)
                || !party.Members.ContainsKey(source.SessionId))
                return;
            recipients = party.Members.Values
                .Where(member => member.Session.SessionId != source.SessionId)
                .Select(member => _activeWorldSessions.TryGetValue(member.Session.SessionId, out var presence)
                    ? presence
                    : null)
                .Where(presence => presence is not null)
                .Cast<WorldPresence>()
                .ToArray();
        }

        foreach (var target in recipients)
        {
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                target,
                opcode,
                payload.ToArray(),
                reason));
        }
    }

    private void QueueSceneBroadcast(
        ConnectionSession source,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        string reason)
    {
        if (source.TradeRoomId != 0)
            QueueTradeRoomBroadcast(source, opcode, payload, reason);
        else if (source.DungeonRoomId != 0)
            QueueDungeonBroadcast(source, opcode, payload, false, reason);
        else if (source.ApartmentOwnerCharacterId > 0)
            QueueApartmentBroadcast(source, opcode, payload, reason);
        else if (source.VillageShopCode != 0)
            QueueVillageShopBroadcast(source, opcode, payload, reason);
        else
            QueueTownBroadcast(source, opcode, payload, reason);
    }

    private void QueueMikeBroadcast(
        ConnectionSession source,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        bool global)
    {
        foreach (var target in _activeWorldSessions.Values
                     .Where(item => item.Session.OnlineTracked)
                     .Where(item => item.SessionId != source.SessionId)
                     .Where(item => global || item.ChannelId == source.ChannelId))
        {
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                target,
                opcode,
                payload.ToArray(),
                global ? "global mike chat" : "channel mike chat"));
        }
    }

    private async Task VillageBotLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await ReconcileVillageBotsAsync(token);
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log($"村庄机器人循环异常停止：{ex.Message}");
        }
    }

    private async Task ReconcileVillageBotsAsync(CancellationToken token)
    {
        await _villageBotReconcileGate.WaitAsync(token);
        try
        {
            await ReconcileVillageBotsCoreAsync(token);
        }
        finally
        {
            _villageBotReconcileGate.Release();
        }
    }

    private async Task ReconcileVillageBotsCoreAsync(CancellationToken token)
    {
        var visibleGroups = _activeWorldSessions.Values
            .Where(item => item.Session.OnlineTracked
                           && item.Session.TownSceneActive
                           && item.Session.Character is not null)
            .GroupBy(item => new TownInstanceKey(
                item.Session.ChannelId,
                item.Session.TownId,
                item.Session.TownPage))
            .OrderBy(group => group.Key.ChannelId)
            .ThenBy(group => group.Key.TownId)
            .ThenBy(group => group.Key.TownPage)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var notifications = new List<PendingNativeBroadcast>();
        var transitions = new List<(VillageBot Bot, TownInstanceKey Source, VillageBotPortal Portal)>();
        lock (_villageBotGate)
        {
            if (!_villageBotSettings.Enabled)
            {
                foreach (var group in _villageBotGroups.Values)
                    foreach (var bot in group)
                        QueueVillageBotLeave(bot, visibleGroups, notifications);
                _villageBotGroups.Clear();
                _villageBotRecentMessages.Clear();
            }
            else
            {
                foreach (var key in visibleGroups.Keys)
                    if (!_villageBotGroups.ContainsKey(key))
                        _villageBotGroups[key] = [];

                foreach (var (key, bots) in _villageBotGroups.ToArray())
                {
                    var viewers = visibleGroups.TryGetValue(key, out var groupViewers)
                        ? groupViewers
                        : [];
                    ResizeVillageBotGroup(key, bots, viewers, notifications);
                    if (viewers.Length == 0)
                    {
                        foreach (var bot in bots)
                        {
                            if (bot.NextSpeechAtTick <= Environment.TickCount64)
                                ScheduleVillageBotSpeech(bot);
                            if (bot.NextEmotionAtTick <= Environment.TickCount64)
                                ScheduleVillageBotEmotion(bot);
                        }
                    }

                    var speechSent = false;
                    var emotionSent = false;
                    foreach (var bot in bots.ToArray())
                    {
                        if (!_villageBotSettings.PortalTravelEnabled)
                            bot.PendingPortal = null;
                        if (!_villageBotSettings.PauseEnabled)
                            bot.PauseTicks = 0;

                        if (TryGetCompletedVillageBotPortal(bot, out var portal))
                        {
                            transitions.Add((bot, key, portal));
                            continue;
                        }

                        if (_villageBotSettings.SpeechEnabled
                            && viewers.Length > 0
                            && !speechSent
                            && bot.NextSpeechAtTick <= Environment.TickCount64)
                        {
                            var chatText = CreateVillageBotMessage(
                                key,
                                recent => VillageBotLanguageCatalog.CreateIdlePhrase(recent));
                            var chatPayload = BuildVillageBotChatPayload(bot, chatText);
                            foreach (var presence in viewers)
                                notifications.Add(new PendingNativeBroadcast(
                                    presence,
                                    0xCB23,
                                    chatPayload.ToArray(),
                                    $"village bot speech name={bot.Character.Name}"));
                            ScheduleVillageBotSpeech(bot);
                            speechSent = true;
                        }

                        if (_villageBotSettings.EmotionEnabled
                            && viewers.Length > 0
                            && !emotionSent
                            && (!_villageBotSettings.MovementEnabled || bot.PauseTicks > 0)
                            && bot.PendingPortal is null
                            && bot.NextEmotionAtTick <= Environment.TickCount64)
                        {
                            var emotionPayload = BuildVillageBotEmotionPayload(bot);
                            foreach (var presence in viewers)
                                notifications.Add(new PendingNativeBroadcast(
                                    presence,
                                    0xCB22,
                                    emotionPayload.ToArray(),
                                    $"village bot emotion name={bot.Character.Name} value={emotionPayload[2]}"));
                            ScheduleVillageBotEmotion(bot);
                            emotionSent = true;
                        }

                        if (!_villageBotSettings.MovementEnabled
                            || !MoveVillageBotOneStep(bot, out var direction, out var distance))
                            continue;
                        var movement = BuildVillageBotMovementPayload(bot, direction, distance);
                        foreach (var presence in viewers)
                            notifications.Add(new PendingNativeBroadcast(
                                presence,
                                0xCB21,
                                movement.ToArray(),
                                $"village bot movement name={bot.Character.Name}"));
                    }
                }

                foreach (var transition in transitions)
                    ApplyVillageBotTransition(transition.Bot, transition.Source, transition.Portal, visibleGroups, notifications);
            }
        }

        await SendNativeBroadcastBatchAsync(notifications, token);
    }

    private void ResizeVillageBotGroup(
        TownInstanceKey key,
        List<VillageBot> bots,
        WorldPresence[] viewers,
        List<PendingNativeBroadcast> notifications)
    {
        var occupiedIds = viewers
            .Select(item => item.Session.Character)
            .Where(item => item is not null)
            .Select(item => GetSceneEntityId(item!))
            .ToHashSet();
        foreach (var bot in bots)
        {
            if (!occupiedIds.Add(bot.EntityId))
            {
                foreach (var viewer in viewers)
                    notifications.Add(new PendingNativeBroadcast(
                        viewer,
                        0xC36B,
                        BuildTownLeavePayload(bot.EntityId),
                        $"village bot entity id changed name={bot.Character.Name}"));
                bot.EntityId = AllocateVillageBotEntityId(occupiedIds);
                occupiedIds.Add(bot.EntityId);
                QueueVillageBotSpawn(bot, viewers, notifications);
            }
        }

        while (bots.Count > _villageBotSettings.Count)
        {
            var bot = bots.OrderBy(item => item.LastTransitionAtTick).First();
            foreach (var viewer in viewers)
                notifications.Add(new PendingNativeBroadcast(
                    viewer,
                    0xC36B,
                    BuildTownLeavePayload(bot.EntityId),
                    $"village bot removed name={bot.Character.Name}"));
            bots.Remove(bot);
        }

        while (bots.Count < _villageBotSettings.Count)
        {
            var index = bots.Count;
            var entityId = AllocateVillageBotEntityId(occupiedIds);
            occupiedIds.Add(entityId);
            var gender = Random.Shared.Next(2);
            var seed = HashCode.Combine(key.ChannelId, key.TownId, key.TownPage, index);
            var spawnX = (ushort)(64 + (Math.Abs(seed % 11) * 64));
            var spawnY = (ushort)(64 + (Math.Abs((seed / 997) % 7) * 64));
            var level = Random.Shared.Next(5, 40);
            var reservedNames = _villageBotGroups.Values
                .SelectMany(group => group)
                .Select(item => item.Character.Name)
                .ToHashSet(StringComparer.Ordinal);
            var character = CreateRandomVillageBotCharacter(
                -(index + 1L),
                VillageBotNameCatalog.Create(gender, reservedNames),
                gender,
                level,
                spawnX,
                spawnY);
            var bot = new VillageBot
            {
                EntityId = entityId,
                CharacterUid = AllocateVillageBotCharacterUid(
                    bots.Select(item => item.CharacterUid).ToHashSet()),
                Instance = key,
                HomeX = spawnX,
                HomeY = spawnY,
                Character = character
            };
            bot.NextMoveAtTick = Environment.TickCount64 + Random.Shared.Next(0, 251);
            bot.PauseTicks = _villageBotSettings.PauseEnabled
                ? ChooseVillageBotPauseTicks(initialSpawn: true)
                : 0;
            ScheduleVillageBotSpeech(bot);
            ScheduleVillageBotEmotion(bot);
            ChooseVillageBotTarget(bot);
            bots.Add(bot);
            QueueVillageBotSpawn(bot, viewers, notifications);
        }
    }

    private void ScheduleVillageBotSpeech(VillageBot bot)
    {
        var seconds = Random.Shared.Next(
            _villageBotSettings.MinimumSpeechSeconds,
            _villageBotSettings.MaximumSpeechSeconds + 1);
        bot.NextSpeechAtTick = Environment.TickCount64 + seconds * 1000L;
    }

    private static void ScheduleVillageBotEmotion(VillageBot bot)
        => bot.NextEmotionAtTick = Environment.TickCount64 + Random.Shared.Next(20_000, 75_001);

    private static bool TryGetCompletedVillageBotPortal(
        VillageBot bot,
        out VillageBotPortal portal)
    {
        if (bot.PendingPortal is { } pending
            && bot.Character.PositionX == pending.ExitX
            && bot.Character.PositionY == pending.ExitY)
        {
            portal = pending;
            return true;
        }
        portal = default;
        return false;
    }

    private void ApplyVillageBotTransition(
        VillageBot bot,
        TownInstanceKey sourceKey,
        VillageBotPortal portal,
        IReadOnlyDictionary<TownInstanceKey, WorldPresence[]> visibleGroups,
        ICollection<PendingNativeBroadcast> notifications)
    {
        if (!_villageBotGroups.TryGetValue(sourceKey, out var sourceBots)
            || !sourceBots.Remove(bot))
            return;

        QueueVillageBotLeave(bot, visibleGroups, notifications);
        var destinationKey = new TownInstanceKey(
            sourceKey.ChannelId,
            sourceKey.TownId,
            portal.DestinationPage);
        if (!_villageBotGroups.TryGetValue(destinationKey, out var destinationBots))
            _villageBotGroups[destinationKey] = destinationBots = [];

        var destinationViewers = visibleGroups.TryGetValue(destinationKey, out var viewers)
            ? viewers
            : [];
        var occupiedEntityIds = destinationBots.Select(item => item.EntityId)
            .Concat(destinationViewers
                .Select(item => item.Session.Character)
                .Where(item => item is not null)
                .Select(item => GetSceneEntityId(item!)))
            .ToHashSet();
        var occupiedCharacterUids = destinationBots.Select(item => item.CharacterUid).ToHashSet();
        bot.EntityId = AllocateVillageBotEntityId(occupiedEntityIds);
        bot.CharacterUid = AllocateVillageBotCharacterUid(occupiedCharacterUids);
        bot.Instance = destinationKey;
        bot.Character.PositionX = portal.EntryX;
        bot.Character.PositionY = portal.EntryY;
        bot.HomeX = portal.EntryX;
        bot.HomeY = portal.EntryY;
        bot.TargetX = portal.EntryX;
        bot.TargetY = portal.EntryY;
        bot.PendingPortal = null;
        bot.Direction = 4;
        bot.LastDirection = 4;
        bot.PauseTicks = _villageBotSettings.PauseEnabled
            ? ChooseVillageBotPauseTicks(initialSpawn: true)
            : 0;
        bot.NextMoveAtTick = Environment.TickCount64 + 250;
        bot.LastTransitionAtTick = Environment.TickCount64;
        ScheduleVillageBotSpeech(bot);
        ScheduleVillageBotEmotion(bot);
        destinationBots.Add(bot);
        QueueVillageBotSpawn(bot, destinationViewers, notifications);
    }

    private static ushort AllocateVillageBotCharacterUid(ISet<ushort> occupiedIds)
    {
        for (var value = 0xF000; value <= 0xF0FF; value++)
        {
            var candidate = (ushort)value;
            if (!occupiedIds.Contains(candidate))
                return candidate;
        }
        throw new InvalidOperationException("村庄机器人没有可用的角色 UID。");
    }

    private static CharacterRecord CreateRandomVillageBotCharacter(
        long id,
        string name,
        int gender,
        int level,
        ushort spawnX,
        ushort spawnY)
    {
        var appearance = DatabaseService.CreateDefaultAppearance(gender);
        var clothingByPart = ShopCatalog.All
            .Where(item => item.Section == InventorySection.Clothing
                           && item.ItemCode / 1_000_000u == 10u
                           && IsVillageBotClothingCompatible(item.ItemCode, gender))
            .GroupBy(item => (int)((item.ItemCode / 10_000u) % 10u))
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var (part, offset) in VillageBotAppearanceOffsets)
        {
            if (!clothingByPart.TryGetValue(part, out var choices) || choices.Length == 0)
                continue;
            if (part >= 4 && Random.Shared.Next(100) < 45)
                continue;
            var item = choices[Random.Shared.Next(choices.Length)];
            BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(offset, sizeof(uint)), item.ItemCode);
        }

        var petChoices = ShopCatalog.All
            .Where(item => item.Section == InventorySection.Pet
                           && item.ItemCode / 1_000_000u == 15u)
            .ToArray();
        var pet = petChoices.Length == 0 ? null : petChoices[Random.Shared.Next(petChoices.Length)];
        var petLevel = (uint)Random.Shared.Next(1, Math.Min(level, 30) + 1);
        var character = new CharacterRecord
        {
            Id = id,
            Name = name,
            Gender = gender,
            Level = level,
            CurrentHp = 160 + level * 8,
            MaxHp = 160 + level * 8,
            CurrentMp = 100 + level * 5,
            MaxMp = 100 + level * 5,
            PositionX = spawnX,
            PositionY = spawnY,
            Appearance = appearance,
            PetLevel = (int)petLevel,
            EquippedPetItemCode = pet?.ItemCode ?? 0
        };

        if (pet is not null)
        {
            var currentStage = pet.PetModelStage > 0 ? pet.PetModelStage : (byte)1;
            var maximumStage = Math.Max(currentStage, pet.PetUpgradeStage);
            character.Items.Add(new CharacterItemRecord
            {
                ItemCode = pet.ItemCode,
                Quantity = 1,
                PetDurability = pet.PetMaxDurability >= 0 ? pet.PetMaxDurability : null,
                PetCurrentStage = currentStage,
                PetMaximumStage = maximumStage,
                PetLevel = petLevel,
                PetExperience = 0
            });
        }

        character.Appearance = DatabaseService.NormalizeAppearanceForGender(
            character.Appearance,
            gender,
            character.EquippedPetItemCode);
        return character;
    }

    private static readonly (int Part, int Offset)[] VillageBotAppearanceOffsets =
    [
        (3, 0), (0, 4), (1, 8), (2, 12), (4, 16), (5, 20), (6, 24)
    ];

    private static bool IsVillageBotClothingCompatible(uint itemCode, int gender)
    {
        var itemGender = (itemCode / 100_000u) % 10u;
        return itemGender > 1u || itemGender == (uint)gender;
    }

    private static string GetVillageBotPetName(CharacterRecord character)
    {
        var itemCode = GetEquippedPetItemCode(character);
        return itemCode != 0 && ShopCatalog.TryGet(itemCode, out var item)
            ? item.Name
            : "未佩戴";
    }

    private static string GetVillageBotOutfitName(CharacterRecord character)
    {
        var appearance = BuildStoredAppearance(character);
        var names = new List<string>();
        foreach (var (_, offset) in VillageBotAppearanceOffsets)
        {
            var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(offset, sizeof(uint)));
            if (itemCode != 0 && ShopCatalog.TryGet(itemCode, out var item))
                names.Add(item.Name);
        }
        return names.Count == 0 ? "默认外观" : string.Join(" / ", names);
    }

    private void QueueVillageBotConversationReply(
        ConnectionSession source,
        string chatText)
    {
        if (!source.TownSceneActive || source.Character is null)
            return;

        byte[]? replyPayload = null;
        string? botName = null;
        lock (_villageBotGate)
        {
            if (!_villageBotSettings.Enabled || !_villageBotSettings.ConversationEnabled)
                return;
            var key = new TownInstanceKey(source.ChannelId, source.TownId, source.TownPage);
            if (!_villageBotGroups.TryGetValue(key, out var bots) || bots.Count == 0)
                return;

            var now = Environment.TickCount64;
            var namedBot = bots.FirstOrDefault(item =>
                chatText.Contains(item.Character.Name, StringComparison.OrdinalIgnoreCase));
            var looksLikeQuestion = chatText.Contains("???", StringComparison.OrdinalIgnoreCase)
                                    || chatText.Contains("???", StringComparison.OrdinalIgnoreCase)
                                    || chatText.Contains('?')
                                    || chatText.Contains('?')
                                    || chatText.Contains('?')
                                    || chatText.Contains("???", StringComparison.OrdinalIgnoreCase)
                                    || chatText.Contains("??", StringComparison.OrdinalIgnoreCase)
                                    || chatText.Contains("??", StringComparison.OrdinalIgnoreCase);
            if (namedBot is null && !looksLikeQuestion && Random.Shared.Next(100) >= 12)
                return;

            var bot = namedBot is not null && namedBot.NextReplyAtTick <= now
                ? namedBot
                : bots.Where(item => item.NextReplyAtTick <= now)
                    .OrderBy(_ => Random.Shared.Next())
                    .FirstOrDefault();
            if (bot is null)
                return;

            bot.NextReplyAtTick = now + 8_000;
            botName = bot.Character.Name;
            var replyText = CreateVillageBotMessage(
                key,
                recent => VillageBotLanguageCatalog.CreateReply(source.Character.Name, chatText, recent));
            replyPayload = BuildVillageBotChatPayload(bot, replyText);
        }

        if (replyPayload is null)
            return;
        foreach (var target in _activeWorldSessions.Values.Where(item =>
                     item.Session.OnlineTracked
                     && item.Session.TownSceneActive
                     && item.Session.ChannelId == source.ChannelId
                     && item.Session.TownId == source.TownId
                     && item.Session.TownPage == source.TownPage))
        {
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                target,
                0xCB23,
                replyPayload.ToArray(),
                $"village bot conversation name={botName}"));
        }
    }

    private bool TryQueueVillageBotWhisperReply(
        ConnectionSession source,
        string targetName,
        string chatText)
    {
        if (!source.TownSceneActive || source.Character is null)
            return false;

        byte[]? replyPayload = null;
        lock (_villageBotGate)
        {
            if (!_villageBotSettings.Enabled || !_villageBotSettings.ConversationEnabled)
                return false;
            var key = new TownInstanceKey(source.ChannelId, source.TownId, source.TownPage);
            if (!_villageBotGroups.TryGetValue(key, out var bots))
                return false;
            var bot = bots.FirstOrDefault(item =>
                string.Equals(item.Character.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (bot is null || bot.NextReplyAtTick > Environment.TickCount64)
                return false;

            bot.NextReplyAtTick = Environment.TickCount64 + 8_000;
            var replyText = CreateVillageBotMessage(
                key,
                recent => VillageBotLanguageCatalog.CreateReply(source.Character.Name, chatText, recent));
            replyPayload = BuildVillageBotChatPayload(
                bot,
                replyText,
                2000,
                source.Character.Name);
        }

        var sourcePresence = _activeWorldSessions.Values.FirstOrDefault(item =>
            item.Session.SessionId == source.SessionId);
        if (replyPayload is null || sourcePresence is null)
            return false;
        source.PendingBroadcasts.Add(new PendingNativeBroadcast(
            sourcePresence,
            0xCB23,
            replyPayload,
            $"village bot whisper reply target={source.Character.Name}"));
        return true;
    }

    private static byte[] BuildVillageBotChatPayload(
        VillageBot bot,
        string text,
        ushort chatType = 1000,
        string whisperTargetName = "")
    {
        var payload = new byte[SceneChatPayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), bot.EntityId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), chatType);
        WriteFixedGbk(payload.AsSpan(4, 16), bot.Character.Name);
        WriteFixedGbk(payload.AsSpan(20, 16), whisperTargetName);
        WriteFixedGbk(payload.AsSpan(36, 64), text);
        return payload;
    }

    private string CreateVillageBotMessage(
        TownInstanceKey key,
        Func<IReadOnlyCollection<string>, string> createMessage)
    {
        if (!_villageBotRecentMessages.TryGetValue(key, out var recent))
            _villageBotRecentMessages[key] = recent = new Queue<string>();

        var message = createMessage(recent.ToArray());
        while (recent.Count >= 12)
            recent.Dequeue();
        recent.Enqueue(message);
        return message;
    }

    private static byte[] BuildVillageBotEmotionPayload(VillageBot bot)
    {
        // Retail CB22 is: scene entity UID, one-byte emotion, one-byte type.
        // Type zero is the facial-expression path; 8 and 16 are both captured
        // from the original client and accepted by its remote-player renderer.
        var payload = new byte[SceneEmotionPayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), bot.EntityId);
        payload[2] = Random.Shared.Next(2) == 0 ? (byte)8 : (byte)16;
        payload[3] = 0;
        return payload;
    }

    private static void QueueVillageBotSpawn(
        VillageBot bot,
        IEnumerable<WorldPresence> viewers,
        ICollection<PendingNativeBroadcast> notifications)
    {
        var payload = BuildTownUserInfoPayload(bot.Character, bot.EntityId, bot.CharacterUid);
        foreach (var viewer in viewers)
            notifications.Add(new PendingNativeBroadcast(
                viewer,
                0xC36A,
                payload.ToArray(),
                $"village bot entered name={bot.Character.Name}"));
    }

    private static ushort AllocateVillageBotEntityId(ISet<ushort> occupiedIds)
    {
        for (var value = 0xFFF; value >= 0xE00; value--)
        {
            var candidate = (ushort)value;
            if (!occupiedIds.Contains(candidate))
                return candidate;
        }
        throw new InvalidOperationException("村庄机器人没有可用的场景实体 ID。");
    }

    private static void QueueVillageBotLeave(
        VillageBot bot,
        IReadOnlyDictionary<TownInstanceKey, WorldPresence[]> visibleGroups,
        ICollection<PendingNativeBroadcast> notifications)
    {
        if (bot.Instance is not { } oldInstance
            || !visibleGroups.TryGetValue(oldInstance, out var viewers))
            return;
        var payload = BuildTownLeavePayload(bot.EntityId);
        foreach (var presence in viewers)
            notifications.Add(new PendingNativeBroadcast(
                presence,
                0xC36B,
                payload.ToArray(),
                $"village bot left name={bot.Character.Name}"));
    }

    private void ChooseVillageBotTarget(VillageBot bot)
    {
        var x = bot.Character.PositionX;
        var y = bot.Character.PositionY;
        if (_villageBotSettings.PortalTravelEnabled
            && bot.PendingPortal is null
            && bot.Instance is { } instance)
        {
            var routes = VillageBotTownNavigation.GetRoutes(instance.TownPage);
            if (routes.Count > 0 && Random.Shared.Next(100) < 8)
                bot.PendingPortal = routes[Random.Shared.Next(routes.Count)];
        }

        if (bot.PendingPortal is { } portal)
        {
            var moveHorizontal = x != portal.ExitX
                                 && (y == portal.ExitY || Random.Shared.Next(2) == 0);
            if (moveHorizontal)
            {
                bot.TargetX = MoveVillageBotTargetToward(x, portal.ExitX);
                bot.TargetY = (ushort)y;
            }
            else
            {
                bot.TargetX = (ushort)x;
                bot.TargetY = MoveVillageBotTargetToward(y, portal.ExitY);
            }
            return;
        }

        var minimumX = (int)VillageBotTownNavigation.MinimumX;
        var maximumX = (int)VillageBotTownNavigation.MaximumX;
        var minimumY = (int)VillageBotTownNavigation.MinimumY;
        var maximumY = (int)VillageBotTownNavigation.MaximumY;

        var candidates = new List<byte>(4);
        if (y - minimumY >= 4)
            candidates.Add(0);
        if (maximumX - x >= 4)
            candidates.Add(1);
        if (maximumY - y >= 4)
            candidates.Add(2);
        if (x - minimumX >= 4)
            candidates.Add(3);

        if (candidates.Count == 0)
        {
            bot.TargetX = (ushort)Math.Clamp(x, minimumX, maximumX);
            bot.TargetY = (ushort)Math.Clamp(y, minimumY, maximumY);
            return;
        }

        if (bot.LastDirection <= 3 && candidates.Count > 1 && Random.Shared.Next(100) < 80)
        {
            var reverseDirection = (byte)((bot.LastDirection + 2) % 4);
            candidates.Remove(reverseDirection);
        }

        var direction = candidates[Random.Shared.Next(candidates.Count)];
        var availableDistance = direction switch
        {
            0 => y - minimumY,
            1 => maximumX - x,
            2 => maximumY - y,
            _ => x - minimumX
        };
        var maximumSteps = Math.Max(1, Math.Min(availableDistance / 4, 48));
        var minimumSteps = Math.Min(16, maximumSteps);
        var distance = Random.Shared.Next(minimumSteps, maximumSteps + 1) * 4;
        bot.TargetX = (ushort)(direction switch
        {
            1 => x + distance,
            3 => x - distance,
            _ => x
        });
        bot.TargetY = (ushort)(direction switch
        {
            0 => y - distance,
            2 => y + distance,
            _ => y
        });
    }

    private static ushort MoveVillageBotTargetToward(int current, ushort destination)
    {
        var distance = destination - current;
        if (Math.Abs(distance) <= 192)
            return destination;
        return (ushort)(current + Math.Sign(distance) * 192);
    }

    private static int ChooseVillageBotPauseTicks(bool initialSpawn = false)
    {
        var roll = Random.Shared.Next(100);
        if (roll < (initialSpawn ? 30 : 18))
            return Random.Shared.Next(24, 81); // 6-20 seconds of standing around.
        if (roll < 60)
            return Random.Shared.Next(8, 21);  // 2-5 seconds at a destination.
        return Random.Shared.Next(3, 9);       // A brief natural pause before walking.
    }

    private bool MoveVillageBotOneStep(
        VillageBot bot,
        out byte direction,
        out int distance)
    {
        direction = 4;
        distance = 0;
        var now = Environment.TickCount64;
        if (now < bot.NextMoveAtTick)
            return false;
        bot.NextMoveAtTick = now + 250;

        if (_villageBotSettings.PauseEnabled && bot.PauseTicks > 0)
        {
            bot.PauseTicks--;
            return false;
        }

        var x = bot.Character.PositionX;
        var y = bot.Character.PositionY;
        if (bot.TargetX == x && bot.TargetY == y)
        {
            bot.LastDirection = bot.Direction;
            bot.Direction = 4;
            ChooseVillageBotTarget(bot);
            bot.PauseTicks = _villageBotSettings.PauseEnabled && bot.PendingPortal is null
                ? ChooseVillageBotPauseTicks()
                : 0;
            return false;
        }

        var currentDirectionStillValid = bot.Direction switch
        {
            0 or 2 => bot.TargetY != y,
            1 or 3 => bot.TargetX != x,
            _ => false
        };
        if (!currentDirectionStillValid)
        {
            var chooseHorizontal = bot.TargetX != x
                                   && (bot.TargetY == y || Random.Shared.Next(2) == 0);
            bot.Direction = chooseHorizontal
                ? (bot.TargetX > x ? (byte)1 : (byte)3)
                : (bot.TargetY > y ? (byte)2 : (byte)0);
        }

        var moveHorizontally = bot.Direction is 1 or 3;
        if (moveHorizontally)
        {
            direction = bot.Direction;
            distance = Math.Min(64, Math.Abs(bot.TargetX - x));
            x += direction == 1 ? distance : -distance;
        }
        else
        {
            direction = bot.Direction;
            distance = Math.Min(64, Math.Abs(bot.TargetY - y));
            y += direction == 2 ? distance : -distance;
        }

        bot.Character.PositionX = x;
        bot.Character.PositionY = y;
        return true;
    }

    private static byte[] BuildVillageBotMovementPayload(
        VillageBot bot,
        byte direction,
        int distance)
    {
        // CB21 contains sixteen four-bit movement samples followed by X, Y,
        // the town page and the scene entity ID. Each sample is four pixels;
        // unused samples must be idle so a short final step ends at the door.
        var payload = new byte[16];
        payload.AsSpan(0, 8).Fill(0x44);
        var movementSamples = Math.Clamp(distance / 4, 0, 16);
        for (var sample = 0; sample < movementSamples; sample++)
        {
            var index = sample / 2;
            payload[index] = sample % 2 == 0
                ? (byte)((payload[index] & 0xF0) | direction)
                : (byte)((payload[index] & 0x0F) | (direction << 4));
        }
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(8, 2),
            (ushort)bot.Character.PositionX);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(10, 2),
            (ushort)bot.Character.PositionY);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(12, 2),
            bot.Instance?.TownPage ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14, 2), bot.EntityId);
        return payload;
    }

    private void LeaveApartmentScene(ConnectionSession session, string reason)
    {
        if (session.ApartmentOwnerCharacterId <= 0)
            return;

        if (session.Character is not null)
        {
            QueueApartmentBroadcast(
                session,
                0xC391,
                BuildRoomEntityLeavePayload(session.Character),
                reason);
        }
        session.ApartmentOwnerCharacterId = 0;
    }

    private void LeaveVillageShopScene(ConnectionSession session, string reason)
    {
        if (session.VillageShopCode == 0)
            return;

        if (session.Character is not null)
        {
            QueueVillageShopBroadcast(
                session,
                0xC3AF,
                BuildRoomEntityLeavePayload(session.Character),
                reason);
        }
        session.VillageShopCode = 0;
    }

    private void LeaveTownScene(ConnectionSession session, string reason)
    {
        if (!session.TownSceneActive || session.Character is null)
        {
            session.TownMapMarkerInitialized = false;
            return;
        }

        QueueTownBroadcast(
            session,
            0xC36B,
            BuildTownLeavePayload(session.Character),
            reason);
        session.TownSceneActive = false;
        session.TownMapMarkerInitialized = false;
    }

    private void QueueTownDisconnectNotification(ConnectionSession session)
        => LeaveTownScene(session, "town connection leave");

    private void QueueTownEntitySnapshots(ConnectionSession source)
    {
        if (source.Character is null
            || !_activeWorldSessions.TryGetValue(source.SessionId, out var sourcePresence))
            return;

        if (!source.TownMapMarkerInitialized)
        {
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                sourcePresence,
                0xC36D,
                BuildTownMapMarkerPayload(source.Character),
                "initialize local town map gender marker"));
            source.TownMapMarkerInitialized = true;
        }

        // Before the first C365/C367 the native client can request a town
        // entity immediately after C355.  The character record still carries
        // the last persisted position, but the session has already selected
        // the safe first-process wire position (400,96).  C36A must use the
        // session carrier here; otherwise the client receives a stale/poisoned
        // packed position and constructs CVillageChar(-1,-1) at the upper-left.
        var sourcePayload = BuildTownUserInfoPayload(
            source.Character,
            source.LastReportedPositionX,
            source.LastReportedPositionY);
        foreach (var peer in _activeWorldSessions.Values
                     .Where(item => IsSameTownPage(source, item.Session))
                     .Where(item => item.Session.SessionId != source.SessionId))
        {
            var peerCharacter = peer.Session.Character;
            if (peerCharacter is null)
                continue;

            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                peer,
                0xC36A,
                sourcePayload.ToArray(),
                "town entity entered"));
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                sourcePresence,
                0xC36A,
                BuildTownUserInfoPayload(
                    peerCharacter,
                    peer.Session.LastReportedPositionX,
                    peer.Session.LastReportedPositionY),
                "existing town entity snapshot"));
        }

        var sourceKey = new TownInstanceKey(source.ChannelId, source.TownId, source.TownPage);
        lock (_villageBotGate)
        {
            if (!_villageBotGroups.TryGetValue(sourceKey, out var bots))
                return;
            foreach (var bot in bots)
            {
                source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    sourcePresence,
                    0xC36A,
                    BuildTownUserInfoPayload(bot.Character, bot.EntityId, bot.CharacterUid),
                    $"existing village bot snapshot name={bot.Character.Name}"));
            }
        }
    }

    private void QueueApartmentEntitySnapshots(
        ConnectionSession source,
        ReadOnlySpan<byte> sourcePayload)
    {
        if (source.Character is null
            || source.ApartmentOwnerCharacterId <= 0
            || !_activeWorldSessions.TryGetValue(source.SessionId, out var sourcePresence))
            return;

        foreach (var peer in _activeWorldSessions.Values
                     .Where(item => IsSameApartmentRoom(source, item.Session)))
        {
            var peerCharacter = peer.Session.Character;
            if (peerCharacter is null)
                continue;

            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                peer,
                0xC390,
                sourcePayload.ToArray(),
                "apartment entity entered"));
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                sourcePresence,
                0xC390,
                BuildMiniRoomUserInfoPayload(
                    peerCharacter,
                    peer.Session.LastReportedPositionX,
                    peer.Session.LastReportedPositionY),
                "existing apartment entity snapshot"));
        }
    }

    private void QueueVillageShopEntitySnapshots(
        ConnectionSession source,
        ReadOnlySpan<byte> sourcePayload)
    {
        if (source.Character is null
            || source.VillageShopCode == 0
            || !_activeWorldSessions.TryGetValue(source.SessionId, out var sourcePresence))
            return;

        foreach (var peer in _activeWorldSessions.Values
                     .Where(item => IsSameVillageShop(source, item.Session)))
        {
            var peerCharacter = peer.Session.Character;
            if (peerCharacter is null)
                continue;

            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                peer,
                0xC3AE,
                sourcePayload.ToArray(),
                "shop entity entered"));
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                sourcePresence,
                0xC3AE,
                BuildShopUserInfoPayload(peerCharacter),
                "existing shop entity snapshot"));
        }
    }

    private WorldPresence? FindVisibleScenePeer(ConnectionSession source, uint entityUid)
        => _activeWorldSessions.Values.FirstOrDefault(item =>
            item.Session.Character is { } character
            && (GetSceneEntityId(character) == entityUid || character.Id == entityUid)
            && (IsSameTownPage(source, item.Session)
                || IsSameApartmentRoom(source, item.Session)
                || IsSameVillageShop(source, item.Session)));

    private async Task<byte[]?> HandleCoupleRequestAsync(
        byte[] frame,
        ushort opcode,
        byte[] payload,
        ConnectionSession session,
        string channel,
        string remote,
        CancellationToken token)
    {
        if (!session.OnlineTracked
            || session.Character is null
            || !_activeWorldSessions.TryGetValue(session.SessionId, out var requester)
            || payload.Length != CoupleProtocol.GetPayloadLength(opcode)
            || !CoupleProtocol.TryReadPeerName(payload, out var requestedPeerName))
            return null;

        var itemCode = CoupleProtocol.ReadItemCode(payload);
        var inventorySlot = CoupleProtocol.ReadInventorySlot(opcode, payload);
        var expectedSource = opcode == CoupleProtocol.RingRequestOpcode
            ? "CI._D28/COUPLERING"
            : "CI._D28/COUPLECANCEL";
        await RefreshSessionCharacterAsync(session, token);
        var gameItems = GetGameInventoryItemCodes(session.Character);
        if (inventorySlot >= gameItems.Length
            || gameItems[inventorySlot] != itemCode
            || !ShopCatalog.TryGet(itemCode, out var coupleItem)
            || coupleItem.Category != 43
            || !string.Equals(coupleItem.Source, expectedSource, StringComparison.Ordinal))
        {
            _log($"{channel}:{remote} couple request inventory fields invalid: opcode=0x{opcode:X4} item={itemCode} slot={inventorySlot}/{gameItems.Length}; inventory unchanged");
            return BuildNativeFrame(
                frame,
                CoupleProtocol.GetResponseOpcode(opcode),
                CoupleProtocol.BuildResponse(
                    CoupleProtocol.GetResponseOpcode(opcode), requestedPeerName,
                    itemCode, inventorySlot, CoupleProtocol.Unavailable, session.Character!),
                session);
        }

        var responder = FindCoupleRequestTarget(session, requestedPeerName);
        if (opcode == CoupleProtocol.SeparationRequestOpcode && itemCode == 43_100_002u)
        {
            var relation = await _database.GetActiveCoupleRelationAsync(session.Character!.Id, token);
            if (relation is null
                || !string.Equals(
                    relation.GetPartnerName(session.Character.Id),
                    requestedPeerName,
                    StringComparison.Ordinal))
            {
                _log($"{channel}:{remote} forced separation rejected: requester={session.Character.Name} target={requestedPeerName}; active relation not found");
                return BuildNativeFrame(
                    frame, CoupleProtocol.SeparationResponseOpcode,
                    CoupleProtocol.BuildResponse(
                        CoupleProtocol.SeparationResponseOpcode, requestedPeerName,
                        itemCode, inventorySlot, CoupleProtocol.Unavailable, session.Character),
                    session);
            }

            var partner = responder?.Session.Character
                ?? await _database.GetCharacterByIdAsync(relation.GetPartnerId(session.Character.Id), token);
            if (partner is null)
                return null;
            var ended = await _database.EndCoupleRelationAsync(
                session.AccountId, session.Character.Id, session.SessionId,
                partner.Id, itemCode, token);
            var resultStatus = ended.Success ? CoupleProtocol.Accepted : CoupleProtocol.Unavailable;
            if (ended.Success)
            {
                await RefreshSessionCharacterAsync(session, token);
                if (responder is not null)
                    await RefreshSessionCharacterAsync(responder.Session, token);
                if (responder is not null && responder.Session.Character is not null)
                {
                    session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                        responder,
                        CoupleProtocol.SeparationResponseOpcode,
                        CoupleProtocol.BuildResponse(
                            CoupleProtocol.SeparationResponseOpcode,
                            session.Character!.Name,
                            itemCode,
                            inventorySlot,
                            CoupleProtocol.Accepted,
                            session.Character),
                        "forced couple separation"));
                }
            }
            _log($"{channel}:{remote} forced couple separation: requester={session.Character!.Name} target={partner.Name} item={itemCode} slot={inventorySlot} result={(ended.Success ? "success" : "failure")} remaining={ended.RemainingQuantity} error={ended.Error}");
            return BuildNativeFrame(
                frame,
                CoupleProtocol.SeparationResponseOpcode,
                CoupleProtocol.BuildResponse(
                    CoupleProtocol.SeparationResponseOpcode,
                    partner.Name,
                    itemCode,
                    inventorySlot,
                    resultStatus,
                    partner),
                session);
        }

        if (responder?.Session.Character is null)
        {
            _log($"{channel}:{remote} couple request target unavailable: opcode=0x{opcode:X4} requester={session.Character!.Name} target={requestedPeerName}");
            return BuildNativeFrame(
                frame,
                CoupleProtocol.GetResponseOpcode(opcode),
                CoupleProtocol.BuildResponse(
                    CoupleProtocol.GetResponseOpcode(opcode), requestedPeerName,
                    itemCode, inventorySlot, CoupleProtocol.Unavailable, session.Character),
                session);
        }

        if (opcode == CoupleProtocol.RingRequestOpcode)
        {
            var requesterRelation = await _database.GetActiveCoupleRelationAsync(session.Character!.Id, token);
            var responderRelation = await _database.GetActiveCoupleRelationAsync(responder.CharacterId, token);
            if (requesterRelation is not null || responderRelation is not null)
            {
                _log($"{channel}:{remote} couple ring request rejected because a participant already has a relation: requester={session.Character.Name} target={responder.CharacterName}");
                return BuildNativeFrame(
                    frame,
                    CoupleProtocol.RingResponseOpcode,
                    CoupleProtocol.BuildResponse(
                        CoupleProtocol.RingResponseOpcode, responder.CharacterName,
                        itemCode, inventorySlot, 7, responder.Session.Character),
                    session);
            }
        }
        else
        {
            var relation = await _database.GetActiveCoupleRelationAsync(session.Character!.Id, token);
            if (relation is null || relation.GetPartnerId(session.Character.Id) != responder.CharacterId)
            {
                _log($"{channel}:{remote} couple separation request rejected because target is not the active partner: requester={session.Character.Name} target={responder.CharacterName}");
                return BuildNativeFrame(
                    frame,
                    CoupleProtocol.SeparationResponseOpcode,
                    CoupleProtocol.BuildResponse(
                        CoupleProtocol.SeparationResponseOpcode, responder.CharacterName,
                        itemCode, inventorySlot, 3, responder.Session.Character),
                    session);
            }
        }

        var responseOpcode = CoupleProtocol.GetResponseOpcode(opcode);
        var pendingKey = new CouplePendingKey(
            responder.SessionId,
            responseOpcode,
            session.Character!.Name);
        var pending = new CouplePendingRequest(
            opcode,
            requester,
            responder,
            itemCode,
            inventorySlot,
            payload.ToArray(),
            DateTime.UtcNow);
        lock (_coupleGate)
        {
            PruneCoupleRequestsLocked();
            if (!_couplePendingRequests.TryAdd(pendingKey, pending))
            {
                _log($"{channel}:{remote} duplicate couple request rejected: opcode=0x{opcode:X4} requester={session.Character.Name} target={responder.CharacterName}");
                return BuildNativeFrame(
                    frame,
                    responseOpcode,
                    CoupleProtocol.BuildResponse(
                        responseOpcode, responder.CharacterName,
                        itemCode, inventorySlot, CoupleProtocol.Unavailable,
                        responder.Session.Character),
                    session);
            }
        }

        session.PendingBroadcasts.Add(new PendingNativeBroadcast(
            responder,
            opcode,
            CoupleProtocol.BuildRequestRelay(opcode, payload, session.Character),
            $"couple request 0x{opcode:X4}"));
        _log($"{channel}:{remote} couple request forwarded: opcode=0x{opcode:X4} requester={session.Character.Name} target={responder.CharacterName} item={itemCode} slot={inventorySlot}");
        return null;
    }

    private async Task<byte[]?> HandleCoupleResponseAsync(
        byte[] frame,
        ushort opcode,
        byte[] payload,
        ConnectionSession session,
        string channel,
        string remote,
        CancellationToken token)
    {
        if (!session.OnlineTracked
            || session.Character is null
            || payload.Length != CoupleProtocol.GetPayloadLength(opcode)
            || !CoupleProtocol.TryReadPeerName(payload, out var requesterName))
            return null;

        var status = CoupleProtocol.ReadStatus(opcode, payload);
        if (!CoupleProtocol.IsOfficialResponseStatus(status))
        {
            _log($"{channel}:{remote} couple response status invalid: opcode=0x{opcode:X4} status={status}");
            return null;
        }

        CouplePendingRequest? pending = null;
        var key = new CouplePendingKey(session.SessionId, opcode, requesterName);
        lock (_coupleGate)
        {
            PruneCoupleRequestsLocked();
            if (_couplePendingRequests.TryGetValue(key, out var candidate)
                && candidate.Responder.SessionId == session.SessionId
                && CoupleProtocol.ReadItemCode(payload) == candidate.ItemCode
                && CoupleProtocol.ReadInventorySlot(opcode, payload) == candidate.InventorySlot)
            {
                pending = candidate;
                _couplePendingRequests.Remove(key);
            }
        }
        if (pending?.Requester.Session.Character is null)
        {
            _log($"{channel}:{remote} couple response has no matching pending request: opcode=0x{opcode:X4} responder={session.Character.Name} requester={requesterName}");
            return null;
        }

        var finalStatus = status;
        if (status == CoupleProtocol.Accepted)
        {
            if (opcode == CoupleProtocol.RingResponseOpcode)
            {
                var created = await _database.CreateCoupleRelationAsync(
                    pending.Requester.AccountId,
                    pending.Requester.CharacterId,
                    pending.Requester.Session.SessionId,
                    pending.Responder.CharacterId,
                    pending.ItemCode,
                    token);
                if (!created.Success)
                    finalStatus = 7;
                _log($"{channel}:{remote} couple ring answer committed: requester={pending.Requester.CharacterName} responder={session.Character.Name} item={pending.ItemCode} status={status} result={(created.Success ? "success" : "failure")} remaining={created.RemainingQuantity} error={created.Error}");
            }
            else
            {
                var ended = await _database.EndCoupleRelationAsync(
                    pending.Requester.AccountId,
                    pending.Requester.CharacterId,
                    pending.Requester.Session.SessionId,
                    pending.Responder.CharacterId,
                    pending.ItemCode,
                    token);
                if (!ended.Success)
                    finalStatus = 3;
                _log($"{channel}:{remote} couple separation answer committed: requester={pending.Requester.CharacterName} responder={session.Character.Name} item={pending.ItemCode} status={status} result={(ended.Success ? "success" : "failure")} remaining={ended.RemainingQuantity} error={ended.Error}");
            }
        }

        if (finalStatus == CoupleProtocol.Accepted)
        {
            await RefreshSessionCharacterAsync(pending.Requester.Session, token);
            await RefreshSessionCharacterAsync(session, token);
        }

        if (_activeWorldSessions.TryGetValue(pending.Requester.SessionId, out var activeRequester)
            && activeRequester.AccountId == pending.Requester.AccountId)
        {
            session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                activeRequester,
                opcode,
                CoupleProtocol.BuildResponse(
                    opcode,
                    session.Character!.Name,
                    pending.ItemCode,
                    pending.InventorySlot,
                    finalStatus,
                    session.Character),
                $"couple response 0x{opcode:X4}"));
        }

        _log($"{channel}:{remote} couple response forwarded: opcode=0x{opcode:X4} responder={session.Character!.Name} requester={pending.Requester.CharacterName} requestedStatus={status} finalStatus={finalStatus}");
        return BuildNativeFrame(
            frame,
            opcode,
            CoupleProtocol.BuildResponse(
                opcode,
                pending.Requester.CharacterName,
                pending.ItemCode,
                pending.InventorySlot,
                finalStatus,
                pending.Requester.Session.Character!),
            session);
    }

    private WorldPresence? FindCoupleRequestTarget(
        ConnectionSession source,
        string requestedPeerName)
        => _activeWorldSessions.Values.FirstOrDefault(item =>
            item.SessionId != source.SessionId
            && item.ChannelId == source.ChannelId
            && item.Session.OnlineTracked
            && item.Session.Character is not null
            && string.Equals(item.CharacterName, requestedPeerName, StringComparison.Ordinal)
            && (IsSameTownPage(source, item.Session)
                || IsSameApartmentRoom(source, item.Session)
                || IsSameVillageShop(source, item.Session)));

    private void PruneCoupleRequestsLocked()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(30);
        foreach (var key in _couplePendingRequests
                     .Where(pair => pair.Value.CreatedAtUtc < cutoff
                                    || !pair.Value.Requester.Session.OnlineTracked
                                    || !pair.Value.Responder.Session.OnlineTracked)
                     .Select(pair => pair.Key)
                     .ToArray())
            _couplePendingRequests.Remove(key);
    }

    private WorldPresence? FindMentorRequestTarget(
        ConnectionSession source,
        ushort requestOpcode,
        string requestedPeerName,
        byte requestedPeerUid)
    {
        var candidates = _activeWorldSessions.Values.Where(item =>
            item.SessionId != source.SessionId
            && item.ChannelId == source.ChannelId
            && item.Session.OnlineTracked
            && item.Session.Character is not null);

        if (requestOpcode == MentorProtocol.StudentRequestOpcode)
        {
            return candidates.FirstOrDefault(item =>
                string.Equals(item.CharacterName, requestedPeerName, StringComparison.Ordinal)
                && _mentorAdvertisingCharacters.ContainsKey(item.CharacterId));
        }

        if (requestOpcode != MentorProtocol.TeacherRequestOpcode)
            return null;
        return candidates.FirstOrDefault(item =>
            MentorProtocol.GetLessonPeerUid(item.Session.Character!) == requestedPeerUid
            && string.Equals(item.CharacterName, requestedPeerName, StringComparison.Ordinal)
            && (IsSameTownPage(source, item.Session)
                || IsSameApartmentRoom(source, item.Session)));
    }

    private WorldPresence? FindPartyInvitationTarget(ConnectionSession source, ushort entityUid)
        => _activeWorldSessions.Values.FirstOrDefault(item =>
            item.Session.Character is { } character
            && GetSceneEntityId(character) == entityUid
            && IsSamePartyInvitationScene(source, item.Session));

    private static bool IsSamePartyInvitationScene(ConnectionSession source, ConnectionSession target)
        => IsSameTownPage(source, target)
           || IsSameApartmentRoom(source, target)
           || IsSameVillageShop(source, target)
           || IsSameTradeRoom(source, target)
           || (source.SessionId != target.SessionId
               && source.OnlineTracked
               && target.OnlineTracked
               && source.DungeonRoomId > 0
               && source.ChannelId == target.ChannelId
               && source.DungeonRoomId == target.DungeonRoomId);

    private static bool IsSameTradeInvitationScene(ConnectionSession source, ConnectionSession target)
        => IsSameTownPage(source, target)
           || IsSameApartmentRoom(source, target)
           || IsSameVillageShop(source, target);

    private bool TryCreateTradeInvitation(
        ConnectionSession inviter,
        ConnectionSession invitee)
    {
        lock (_tradeRoomGate)
        {
            PruneTradeInvitationsLocked();
            if (inviter.SessionId == invitee.SessionId
                || inviter.ChannelId != invitee.ChannelId
                || inviter.TradeRoomId != 0
                || invitee.TradeRoomId != 0
                || inviter.DungeonRoomId != 0
                || invitee.DungeonRoomId != 0)
                return false;

            foreach (var key in _tradeInvitationsByInvitee
                         .Where(item => item.Value.Inviter.SessionId == inviter.SessionId
                                        || item.Value.Invitee.SessionId == inviter.SessionId
                                        || item.Value.Inviter.SessionId == invitee.SessionId
                                        || item.Value.Invitee.SessionId == invitee.SessionId)
                         .Select(item => item.Key)
                         .ToArray())
                _tradeInvitationsByInvitee.Remove(key);

            _tradeInvitationsByInvitee[invitee.SessionId] = new TradeInvitation
            {
                Inviter = inviter,
                Invitee = invitee
            };
            return true;
        }
    }

    private bool TryResolveTradeAgreement(
        ConnectionSession invitee,
        uint inviterEntityUid,
        bool accepted,
        out WorldPresence inviterPresence)
    {
        inviterPresence = null!;
        lock (_tradeRoomGate)
        {
            PruneTradeInvitationsLocked();
            if (!_tradeInvitationsByInvitee.TryGetValue(invitee.SessionId, out var invitation)
                || invitation.Invitee.SessionId != invitee.SessionId
                || invitation.Inviter.Character is not { } inviterCharacter
                || GetSceneEntityId(inviterCharacter) != inviterEntityUid
                || !_activeWorldSessions.TryGetValue(invitation.Inviter.SessionId, out var resolvedInviter)
                || !resolvedInviter.Session.OnlineTracked
                || !invitee.OnlineTracked
                || !IsSameTradeInvitationScene(invitee, invitation.Inviter))
                return false;

            inviterPresence = resolvedInviter;
            if (accepted)
                invitation.Accepted = true;
            else
                _tradeInvitationsByInvitee.Remove(invitee.SessionId);
            return true;
        }
    }

    private bool TryCreateTradeRoom(
        ConnectionSession inviter,
        uint inviteeEntityUid,
        out TradeRoom room,
        out WorldPresence inviteePresence)
    {
        room = null!;
        inviteePresence = null!;
        lock (_tradeRoomGate)
        {
            PruneTradeInvitationsLocked();
            var invitation = _tradeInvitationsByInvitee.Values.FirstOrDefault(item =>
                item.Inviter.SessionId == inviter.SessionId
                && item.Accepted
                && item.Invitee.Character is { } inviteeCharacter
                && GetSceneEntityId(inviteeCharacter) == inviteeEntityUid);
            if (invitation is null
                || inviter.TradeRoomId != 0
                || invitation.Invitee.TradeRoomId != 0
                || inviter.ChannelId != invitation.Invitee.ChannelId
                || !_activeWorldSessions.TryGetValue(invitation.Invitee.SessionId, out var resolvedInvitee)
                || !resolvedInvitee.Session.OnlineTracked
                || !IsSameTradeInvitationScene(inviter, invitation.Invitee))
                return false;

            inviteePresence = resolvedInvitee;
            var roomId = AllocateTradeRoomIdLocked();
            room = new TradeRoom
            {
                Id = roomId,
                ChannelId = inviter.ChannelId,
                Inviter = inviter,
                Invitee = invitation.Invitee
            };
            room.JoinedSessionIds.Add(inviter.SessionId);
            _tradeRooms[roomId] = room;
            inviter.TradeRoomId = roomId;
            _tradeInvitationsByInvitee.Remove(invitation.Invitee.SessionId);
            return true;
        }
    }

    private bool TryEnterTradeRoom(ConnectionSession invitee, int roomId)
    {
        lock (_tradeRoomGate)
        {
            if (!_tradeRooms.TryGetValue(roomId, out var room)
                || room.ChannelId != invitee.ChannelId
                || room.Invitee.SessionId != invitee.SessionId
                || !room.Inviter.OnlineTracked
                || invitee.TradeRoomId != 0)
                return false;

            invitee.TradeRoomId = roomId;
            room.JoinedSessionIds.Add(invitee.SessionId);
            return true;
        }
    }

    private ConnectionSession? FindTradeRoomPeer(ConnectionSession member)
    {
        lock (_tradeRoomGate)
        {
            if (!_tradeRooms.TryGetValue(member.TradeRoomId, out var room)
                || !room.JoinedSessionIds.Contains(member.SessionId))
                return null;
            return room.Inviter.SessionId == member.SessionId
                ? room.Invitee
                : room.Inviter;
        }
    }

    private async Task<byte[]?> TryApplyTradeOfferAsync(
        ConnectionSession member,
        byte[] payload,
        CancellationToken token)
    {
        var putType = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        var peerUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
        var slotIndex = payload[7];
        var card = new TradeCardOffer(
            payload[8],
            payload[9],
            payload[10],
            payload[11],
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12, 4)));
        var hans = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(16, 8));

        IReadOnlyDictionary<uint, byte> ownedCards = new Dictionary<uint, byte>();
        if (putType == TradePutCard)
        {
            if (slotIndex >= TradeCardSlotCount
                || card.Count == 0
                || !CardCatalog.TryGet(card.ItemCode, out var catalogCard)
                || catalogCard.Category != card.Chapter
                || catalogCard.Page != card.Page
                || catalogCard.Slot + 1 != card.Index
                || member.Character is null)
                return null;

            ownedCards = (await _database.GetCharacterCardsAsync(member.Character.Id, token))
                .ToDictionary(item => item.CardCode, item => item.Quantity);
        }

        lock (_tradeRoomGate)
        {
            if (!TryGetJoinedTradeContextLocked(member, out var room, out var peer)
                || peer.Character is null
                || GetSceneEntityId(peer.Character) != peerUid
                || room.Settling
                || room.ReadySessionIds.Count != 0)
                return null;

            if (!room.Offers.TryGetValue(member.SessionId, out var offer))
            {
                offer = new TradeOffer();
                room.Offers[member.SessionId] = offer;
            }

            switch (putType)
            {
                case TradePutCard:
                {
                    var requestedCounts = offer.Cards
                        .Where((item, index) => index != slotIndex && item.HasValue)
                        .Select(item => item!.Value)
                        .GroupBy(item => item.ItemCode)
                        .ToDictionary(group => group.Key, group => group.Sum(item => item.Count));
                    requestedCounts[card.ItemCode] = requestedCounts.GetValueOrDefault(card.ItemCode) + card.Count;
                    if (requestedCounts.Any(item => !ownedCards.TryGetValue(item.Key, out var quantity)
                                                    || item.Value > quantity))
                        return null;
                    offer.Cards[slotIndex] = card;
                    return BuildTradeOfferResultPayload(true, false, slotIndex, card);
                }
                case TradeRemoveCard:
                    if (slotIndex >= TradeCardSlotCount
                        || offer.Cards[slotIndex] is not { } existingCard
                        || existingCard.ItemCode != card.ItemCode)
                        return null;
                    offer.Cards[slotIndex] = null;
                    return BuildTradeOfferResultPayload(true, false, slotIndex, null);
                case TradePutHans:
                    if (hans > uint.MaxValue
                        || member.Character is null
                        || hans > checked((ulong)Math.Max(0, member.Character.Hans)))
                        return null;
                    offer.Hans = hans;
                    return BuildTradeOfferResultPayload(true, true, slotIndex, null);
                default:
                    return null;
            }
        }
    }

    private bool TryReadyTrade(
        ConnectionSession member,
        out WorldPresence peerPresence,
        out byte[] readyPayload)
    {
        peerPresence = null!;
        readyPayload = [];
        lock (_tradeRoomGate)
        {
            if (!TryGetJoinedTradeContextLocked(member, out var room, out var peer)
                || room.Settling
                || !_activeWorldSessions.TryGetValue(peer.SessionId, out var resolvedPeerPresence))
                return false;

            peerPresence = resolvedPeerPresence;
            if (!room.Offers.TryGetValue(member.SessionId, out var offer))
            {
                offer = new TradeOffer();
                room.Offers[member.SessionId] = offer;
            }
            room.ReadySessionIds.Add(member.SessionId);
            room.FinalSessionIds.Remove(member.SessionId);
            readyPayload = BuildTradeReadyPayload(offer);
            return true;
        }
    }

    private bool TryCancelTrade(ConnectionSession member, out WorldPresence peerPresence)
    {
        peerPresence = null!;
        lock (_tradeRoomGate)
        {
            if (!TryGetJoinedTradeContextLocked(member, out var room, out var peer)
                || room.Settling
                || !_activeWorldSessions.TryGetValue(peer.SessionId, out var resolvedPeerPresence))
                return false;
            peerPresence = resolvedPeerPresence;
            room.ReadySessionIds.Clear();
            room.FinalSessionIds.Clear();
            return true;
        }
    }

    private bool TryRequestTradeSettlement(
        ConnectionSession member,
        out WorldPresence peerPresence,
        out TradeSettlementSnapshot? settlement)
    {
        peerPresence = null!;
        settlement = null;
        lock (_tradeRoomGate)
        {
            if (!TryGetJoinedTradeContextLocked(member, out var room, out var peer)
                || room.Settling
                || room.ReadySessionIds.Count != 2
                || !room.ReadySessionIds.Contains(member.SessionId)
                || !_activeWorldSessions.TryGetValue(peer.SessionId, out var resolvedPeerPresence))
                return false;

            peerPresence = resolvedPeerPresence;
            room.FinalSessionIds.Add(member.SessionId);
            if (room.FinalSessionIds.Count != 2)
                return true;

            room.Settling = true;
            settlement = new TradeSettlementSnapshot(
                room.Id,
                room.Inviter,
                room.Offers.GetValueOrDefault(room.Inviter.SessionId)?.Clone() ?? new TradeOffer(),
                room.Invitee,
                room.Offers.GetValueOrDefault(room.Invitee.SessionId)?.Clone() ?? new TradeOffer());
            return true;
        }
    }

    private bool TryGetJoinedTradeContextLocked(
        ConnectionSession member,
        out TradeRoom room,
        out ConnectionSession peer)
    {
        room = null!;
        peer = null!;
        if (member.TradeRoomId == 0
            || !_tradeRooms.TryGetValue(member.TradeRoomId, out var resolvedRoom)
            || resolvedRoom.JoinedSessionIds.Count != 2
            || !resolvedRoom.JoinedSessionIds.Contains(member.SessionId))
            return false;
        room = resolvedRoom;
        peer = room.Inviter.SessionId == member.SessionId ? room.Invitee : room.Inviter;
        return room.JoinedSessionIds.Contains(peer.SessionId)
               && member.OnlineTracked
               && peer.OnlineTracked;
    }

    private void CompleteTradeSettlementState(TradeSettlementSnapshot settlement, uint resultCode)
    {
        lock (_tradeRoomGate)
        {
            if (!_tradeRooms.TryGetValue(settlement.RoomId, out var room)
                || room.Inviter.SessionId != settlement.First.SessionId
                || room.Invitee.SessionId != settlement.Second.SessionId)
                return;
            room.Settling = false;
            room.ReadySessionIds.Clear();
            room.FinalSessionIds.Clear();
            if (resultCode == TradeResultSuccess)
                room.Offers.Clear();
        }
    }

    private static IReadOnlyDictionary<uint, int> BuildTradeCardQuantities(TradeOffer offer)
        => offer.Cards
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .GroupBy(item => item.ItemCode)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count));

    private void LeaveTradeRoomScene(ConnectionSession session, string reason)
    {
        WorldPresence? peerPresence = null;
        lock (_tradeRoomGate)
        {
            foreach (var key in _tradeInvitationsByInvitee
                         .Where(item => item.Value.Inviter.SessionId == session.SessionId
                                        || item.Value.Invitee.SessionId == session.SessionId)
                         .Select(item => item.Key)
                         .ToArray())
                _tradeInvitationsByInvitee.Remove(key);

            if (session.TradeRoomId == 0)
                return;

            if (_tradeRooms.Remove(session.TradeRoomId, out var room))
            {
                var peer = room.Inviter.SessionId == session.SessionId
                    ? room.Invitee
                    : room.Inviter;
                if (room.JoinedSessionIds.Contains(peer.SessionId))
                    _activeWorldSessions.TryGetValue(peer.SessionId, out peerPresence);
                peer.TradeRoomId = 0;
            }
            session.TradeRoomId = 0;
        }

        if (peerPresence is not null)
        {
            session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                peerPresence,
                0xC4C1,
                [],
                reason));
        }
    }

    private int AllocateTradeRoomIdLocked()
    {
        for (var attempt = 0; attempt < ushort.MaxValue; attempt++)
        {
            _nextTradeRoomId = _nextTradeRoomId >= ushort.MaxValue
                ? 1
                : _nextTradeRoomId + 1;
            if (!_tradeRooms.ContainsKey(_nextTradeRoomId))
                return _nextTradeRoomId;
        }
        throw new InvalidOperationException("No trade-room ids are available.");
    }

    private void PruneTradeInvitationsLocked()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-30);
        foreach (var key in _tradeInvitationsByInvitee
                     .Where(item => item.Value.CreatedUtc < cutoff
                                    || !item.Value.Inviter.OnlineTracked
                                    || !item.Value.Invitee.OnlineTracked)
                     .Select(item => item.Key)
                     .ToArray())
            _tradeInvitationsByInvitee.Remove(key);
    }

    private bool TryCreatePartyInvitation(
        ConnectionSession inviter,
        ConnectionSession invitee,
        ReadOnlySpan<byte> inviterMetadata)
    {
        lock (_partyGate)
        {
            PrunePartyInvitationsLocked();
            if (inviter.SessionId == invitee.SessionId
                || inviter.ChannelId != invitee.ChannelId
                || !inviter.OnlineTracked
                || !invitee.OnlineTracked
                || invitee.PartyId != 0
                || _partyInvitationsByInvitee.ContainsKey(invitee.SessionId))
                return false;

            if (inviter.PartyId != 0)
            {
                if (!_parties.TryGetValue(inviter.PartyId, out var existingParty)
                    || existingParty.OwnerSessionId != inviter.SessionId
                    || !existingParty.Members.ContainsKey(inviter.SessionId)
                    || existingParty.Members.Count >= PartyMaximumMembers)
                    return false;
            }

            _partyInvitationsByInvitee[invitee.SessionId] = new PartyInvitation
            {
                Inviter = inviter,
                Invitee = invitee,
                InviterMetadata = inviterMetadata.ToArray()
            };
            return true;
        }
    }

    private bool TryResolvePartyAgreement(
        ConnectionSession invitee,
        ushort inviterEntityUid,
        ushort requestedResult,
        out ushort resultCode,
        out byte[] unionPayload,
        out WorldPresence[] recipients)
    {
        resultCode = PartyAgreementUnavailable;
        unionPayload = [];
        recipients = [];
        lock (_partyGate)
        {
            PrunePartyInvitationsLocked();
            if (!_partyInvitationsByInvitee.Remove(invitee.SessionId, out var invitation)
                || invitation.Invitee.SessionId != invitee.SessionId
                || invitation.Inviter.Character is not { } inviterCharacter
                || GetSceneEntityId(inviterCharacter) != inviterEntityUid
                || !_activeWorldSessions.TryGetValue(invitation.Inviter.SessionId, out var inviterPresence)
                || !inviterPresence.Session.OnlineTracked
                || !invitee.OnlineTracked
                || !IsSamePartyInvitationScene(invitee, invitation.Inviter))
                return false;

            resultCode = requestedResult;
            if (requestedResult == PartyAgreementAccepted)
            {
                Party party;
                if (invitation.Inviter.PartyId == 0)
                {
                    var partyId = AllocatePartyIdLocked();
                    party = new Party
                    {
                        Id = partyId,
                        ChannelId = invitation.Inviter.ChannelId,
                        OwnerSessionId = invitation.Inviter.SessionId,
                        OwnerMetadata = NormalizePartyMetadata(inviterCharacter, invitation.InviterMetadata)
                    };
                    party.Members[invitation.Inviter.SessionId] = new PartyMember(
                        invitation.Inviter,
                        party.NextJoinOrder++);
                    _parties[partyId] = party;
                    invitation.Inviter.PartyId = partyId;
                }
                else if (!_parties.TryGetValue(invitation.Inviter.PartyId, out party!)
                         || party.OwnerSessionId != invitation.Inviter.SessionId)
                {
                    resultCode = PartyAgreementUnavailable;
                    party = null!;
                }

                if (resultCode == PartyAgreementAccepted
                    && (invitee.PartyId != 0
                        || party.ChannelId != invitee.ChannelId
                        || party.Members.Count >= PartyMaximumMembers))
                    resultCode = PartyAgreementUnavailable;

                if (resultCode == PartyAgreementAccepted)
                {
                    party.OwnerMetadata = NormalizePartyMetadata(inviterCharacter, invitation.InviterMetadata);
                    party.Members[invitee.SessionId] = new PartyMember(invitee, party.NextJoinOrder++);
                    invitee.PartyId = party.Id;
                    unionPayload = BuildPartyUnionPayloadLocked(party, checked((byte)PartyAgreementAccepted));
                    recipients = ResolvePartyPresencesLocked(party);
                    RemovePartyInvitationsForSessionLocked(invitee.SessionId);
                    if (party.Members.Count >= PartyMaximumMembers)
                        RemovePartyInvitationsForSessionLocked(party.OwnerSessionId, inviterOnly: true);
                    return true;
                }

                if (invitation.Inviter.PartyId != 0
                    && _parties.TryGetValue(invitation.Inviter.PartyId, out var emptyParty)
                    && emptyParty.Members.Count == 1)
                {
                    _parties.Remove(emptyParty.Id);
                    invitation.Inviter.PartyId = 0;
                }
            }

            unionPayload = BuildPartyUnionPayload(
                inviterCharacter,
                invitation.InviterMetadata,
                [],
                checked((byte)resultCode));
            recipients = [inviterPresence];
            return true;
        }
    }

    private bool TryLeaveParty(
        ConnectionSession exiting,
        byte reason,
        out byte[] selfPayload,
        out List<PartyLeaveNotification> notifications)
    {
        notifications = [];
        var exitingUid = exiting.Character is null ? (ushort)0 : GetSceneEntityId(exiting.Character);
        selfPayload = BuildPartyLeavePayload(0, reason, false, 0, exitingUid);
        lock (_partyGate)
        {
            RemovePartyInvitationsForSessionLocked(exiting.SessionId);
            if (exiting.PartyId == 0
                || !_parties.TryGetValue(exiting.PartyId, out var party)
                || !party.Members.Remove(exiting.SessionId))
            {
                exiting.PartyId = 0;
                return false;
            }

            var ownerChanged = false;
            ushort newOwnerUid = 0;
            if (party.Members.Count >= 2 && party.OwnerSessionId == exiting.SessionId)
            {
                var newOwner = party.Members.Values.OrderBy(member => member.JoinOrder).First();
                party.OwnerSessionId = newOwner.Session.SessionId;
                party.OwnerMetadata = BuildDefaultPartyMetadata(newOwner.Session.Character);
                ownerChanged = true;
                if (newOwner.Session.Character is not null)
                    newOwnerUid = GetSceneEntityId(newOwner.Session.Character);
            }

            if (party.Members.Count < 2)
            {
                foreach (var remaining in party.Members.Values)
                {
                    remaining.Session.PartyId = 0;
                    if (_activeWorldSessions.TryGetValue(remaining.Session.SessionId, out var presence))
                    {
                        notifications.Add(new PartyLeaveNotification(
                            presence,
                            BuildPartyLeavePayload(0, reason, false, 0, exitingUid)));
                    }
                }
                _parties.Remove(party.Id);
            }
            else
            {
                var remainingPayload = BuildPartyLeavePayload(
                    1,
                    reason,
                    ownerChanged,
                    newOwnerUid,
                    exitingUid);
                foreach (var remaining in party.Members.Values)
                {
                    if (_activeWorldSessions.TryGetValue(remaining.Session.SessionId, out var presence))
                        notifications.Add(new PartyLeaveNotification(presence, remainingPayload.ToArray()));
                }
            }

            exiting.PartyId = 0;
            return true;
        }
    }

    private bool TryChangePartyOwner(
        ConnectionSession currentOwner,
        ushort requestedOwnerUid,
        out WorldPresence[] recipients)
    {
        recipients = [];
        lock (_partyGate)
        {
            if (currentOwner.PartyId == 0
                || !_parties.TryGetValue(currentOwner.PartyId, out var party)
                || party.OwnerSessionId != currentOwner.SessionId)
                return false;

            var newOwner = party.Members.Values.FirstOrDefault(member =>
                member.Session.SessionId != currentOwner.SessionId
                && member.Session.Character is { } character
                && GetSceneEntityId(character) == requestedOwnerUid);
            if (newOwner?.Session.Character is null)
                return false;

            party.OwnerSessionId = newOwner.Session.SessionId;
            party.OwnerMetadata = BuildDefaultPartyMetadata(newOwner.Session.Character);
            RemovePartyInvitationsForSessionLocked(currentOwner.SessionId, inviterOnly: true);
            recipients = ResolvePartyPresencesLocked(party);
            return true;
        }
    }

    private bool TryRemovePartyMember(
        ConnectionSession owner,
        ushort targetUid,
        out byte partyState,
        out List<PartyLeaveNotification> notifications)
    {
        partyState = 0;
        notifications = [];
        lock (_partyGate)
        {
            if (owner.PartyId == 0
                || !_parties.TryGetValue(owner.PartyId, out var party)
                || party.OwnerSessionId != owner.SessionId)
                return false;

            var targetMember = party.Members.Values.FirstOrDefault(member =>
                member.Session.SessionId != owner.SessionId
                && member.Session.Character is { } character
                && GetSceneEntityId(character) == targetUid);
            if (targetMember is null
                || !_activeWorldSessions.TryGetValue(targetMember.Session.SessionId, out var targetPresence)
                || !TryLeaveParty(targetMember.Session, 20, out var targetPayload, out var remainingNotifications))
                return false;

            partyState = owner.PartyId == 0 ? (byte)0 : (byte)1;
            notifications.Add(new PartyLeaveNotification(targetPresence, targetPayload));
            notifications.AddRange(remainingNotifications.Where(item => item.Target.SessionId != owner.SessionId));
            return true;
        }
    }

    private void LeavePartyScene(ConnectionSession session, byte reason)
    {
        if (!TryLeaveParty(session, reason, out _, out var notifications))
            return;
        foreach (var notification in notifications)
        {
            session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                notification.Target,
                0xC4E8,
                notification.Payload,
                "party connection leave"));
        }
    }

    private byte[] BuildPartyUnionPayloadLocked(Party party, byte answerCode)
    {
        if (!party.Members.TryGetValue(party.OwnerSessionId, out var ownerMember)
            || ownerMember.Session.Character is not { } ownerCharacter)
            throw new InvalidOperationException("Party owner is missing from the member table.");

        var otherMembers = party.Members.Values
            .Where(member => member.Session.SessionId != party.OwnerSessionId)
            .OrderBy(member => member.JoinOrder)
            .Select(member => member.Session.Character)
            .Where(character => character is not null)
            .Cast<CharacterRecord>()
            .Take(PartyMaximumMembers - 1)
            .ToArray();
        return BuildPartyUnionPayload(ownerCharacter, party.OwnerMetadata, otherMembers, answerCode);
    }

    private WorldPresence[] ResolvePartyPresencesLocked(Party party)
        => party.Members.Values
            .Select(member => _activeWorldSessions.TryGetValue(member.Session.SessionId, out var presence)
                ? presence
                : null)
            .Where(presence => presence is not null)
            .Cast<WorldPresence>()
            .ToArray();

    private int AllocatePartyIdLocked()
    {
        for (var attempt = 0; attempt < int.MaxValue; attempt++)
        {
            _nextPartyId = _nextPartyId == int.MaxValue ? 1 : _nextPartyId + 1;
            if (!_parties.ContainsKey(_nextPartyId))
                return _nextPartyId;
        }
        throw new InvalidOperationException("No party ids are available.");
    }

    private void PrunePartyInvitationsLocked()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-30);
        foreach (var key in _partyInvitationsByInvitee
                     .Where(item => item.Value.CreatedUtc < cutoff
                                    || !item.Value.Inviter.OnlineTracked
                                    || !item.Value.Invitee.OnlineTracked)
                     .Select(item => item.Key)
                     .ToArray())
            _partyInvitationsByInvitee.Remove(key);
    }

    private void RemovePartyInvitationsForSessionLocked(string sessionId, bool inviterOnly = false)
    {
        foreach (var key in _partyInvitationsByInvitee
                     .Where(item => item.Value.Inviter.SessionId == sessionId
                                    || (!inviterOnly && item.Value.Invitee.SessionId == sessionId))
                     .Select(item => item.Key)
                     .ToArray())
            _partyInvitationsByInvitee.Remove(key);
    }

    private DungeonRoom CreateDungeonRoom(ConnectionSession owner, ReadOnlySpan<byte> createRequestPayload)
    {
        lock (_dungeonRoomGate)
        {
            RemoveDungeonRoomMemberLocked(owner);
            var room = new DungeonRoom
            {
                Id = Interlocked.Increment(ref _nextDungeonRoomId),
                ChannelId = owner.ChannelId,
                OwnerSessionId = owner.SessionId,
                CreateRequestPayload = createRequestPayload.ToArray()
            };
            room.Battle = new DungeonBattleInstance(
                room.HdIndex,
                room.Episode,
                room.Dungeon,
                room.Stage,
                DecodeDungeonLogicalDifficulty(room.Dungeon, room.Stage, room.Difficulty));
            room.PendingEpisode = room.BattleEpisode;
            room.PendingDungeon = room.BattleDungeon;
            room.PendingStage = room.BattleStage;
            room.PendingLogicalDifficulty = room.BattleLogicalDifficulty;
            room.Members[owner.SessionId] = new DungeonMember(owner, 0);
            _dungeonRooms[room.Id] = room;
            owner.DungeonRoomId = room.Id;
            owner.DungeonSlotIndex = 0;
            owner.DungeonReady = false;
            owner.DungeonTeamCode = 0;
            owner.DungeonMulticastInitialized = false;
            ResetP2PState(owner);
            room.SlotStates.CopyTo(owner.DungeonSlotStates, 0);
            return room;
        }
    }

    private DungeonRoom? GetDungeonRoom(ConnectionSession member)
    {
        lock (_dungeonRoomGate)
            return _dungeonRooms.GetValueOrDefault(member.DungeonRoomId);
    }

    private DungeonRoom? GetDungeonRoomById(int roomId)
    {
        lock (_dungeonRoomGate)
            return _dungeonRooms.GetValueOrDefault(roomId);
    }

    private static string DecodeDungeonTitle(DungeonRoom room) =>
        DecodeDungeonFixedText(room.CreateRequestPayload.AsSpan(0, Math.Min(24, room.CreateRequestPayload.Length)));

    private static string DecodeDungeonPassword(DungeonRoom room)
    {
        if (room.CreateRequestPayload.Length < DungeonProtocol.CreateRequestLength
            || room.CreateRequestPayload[35] == 0)
            return string.Empty;
        return DecodeDungeonFixedText(room.CreateRequestPayload.AsSpan(36, 8));
    }

    private static ushort DecodeDungeonDifficulty(DungeonRoom room) =>
        room.CreateRequestPayload.Length >= 32
            ? BinaryPrimitives.ReadUInt16LittleEndian(room.CreateRequestPayload.AsSpan(30, 2))
            : (ushort)0;

    private static string DecodeDungeonFixedText(ReadOnlySpan<byte> value)
    {
        var terminator = value.IndexOf((byte)0);
        if (terminator >= 0)
            value = value[..terminator];
        return Encoding.GetEncoding(936).GetString(value).TrimEnd();
    }

    private bool TryJoinDungeonRoom(
        ConnectionSession session,
        int requestedRoomId,
        out DungeonRoom? joinedRoom,
        byte[]? suppliedPassword = null)
    {
        lock (_dungeonRoomGate)
        {
            joinedRoom = null;
            if (!_dungeonRooms.TryGetValue(requestedRoomId, out var room)
                || room.ChannelId != session.ChannelId)
                return false;
            if (room.Members.TryGetValue(session.SessionId, out var existingMember))
            {
                session.DungeonRoomId = room.Id;
                session.DungeonSlotIndex = existingMember.SlotIndex;
                joinedRoom = room;
                return true;
            }
            if (room.Started
                || room.HasPendingTransition
                || suppliedPassword is not null && !DungeonPasswordMatches(room, suppliedPassword))
                return false;

            var slotIndex = Enumerable.Range(1, 2)
                .Where(index => room.SlotStates[index] != 2)
                .FirstOrDefault(index => room.Members.Values.All(member => member.SlotIndex != index));
            if (slotIndex == 0)
                return false;

            RemoveDungeonRoomMemberLocked(session);
            session.DungeonRoomId = room.Id;
            session.DungeonSlotIndex = (byte)slotIndex;
            session.DungeonReady = false;
            session.DungeonTeamCode = 0;
            session.DungeonMulticastInitialized = false;
            ResetP2PState(session);
            room.Members[session.SessionId] = new DungeonMember(session, (byte)slotIndex);
            InvalidatePendingDungeonGameDataLocked(room);
            room.SlotStates.CopyTo(session.DungeonSlotStates, 0);
            joinedRoom = room;
            return true;
        }
    }

    private static bool DungeonPasswordMatches(DungeonRoom room, ReadOnlySpan<byte> suppliedPassword)
    {
        if (room.CreateRequestPayload.Length < 44 || room.CreateRequestPayload[35] == 0)
            return suppliedPassword.IndexOfAnyExcept((byte)0) < 0;
        var expected = room.CreateRequestPayload.AsSpan(36, 8);
        var expectedLength = expected.IndexOf((byte)0);
        if (expectedLength < 0)
            expectedLength = expected.Length;
        var suppliedLength = suppliedPassword.IndexOf((byte)0);
        if (suppliedLength < 0)
            suppliedLength = suppliedPassword.Length;
        return expected[..expectedLength].SequenceEqual(suppliedPassword[..suppliedLength]);
    }

    private DungeonRoom? QuickEnterDungeonRoom(
        ConnectionSession session,
        ReadOnlySpan<byte> requestPayload,
        out bool created)
    {
        created = false;
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(requestPayload.Slice(0, 2));
        if (mode == 20)
        {
            var requestedRoomId = BinaryPrimitives.ReadUInt16LittleEndian(requestPayload.Slice(6, 2));
            return TryJoinDungeonRoom(session, requestedRoomId, out var selected, new byte[8]) ? selected : null;
        }

        // Retail sub_6E9BA0/sub_6E9C20 serialize the four selection getters in
        // this exact order: episode, RealStage, dungeon, difficulty. Captured Dungeon 2
        // traffic is 0A0000000102FFFF and the matching CF6C room stores those
        // selectors at create payload offsets 27, 29, 28 and 30 respectively.
        var requestedEpisode = requestPayload[2];
        var requestedStage = requestPayload[3];
        var requestedDungeon = requestPayload[4];
        var requestedDifficulty = requestPayload[5];
        if (requestedDifficulty >= DungeonDifficultyCount
            || !DungeonCombatCatalog.HasStage(
                0, requestedEpisode, requestedDungeon, requestedStage))
            return null;
        DungeonRoom? available;
        lock (_dungeonRoomGate)
        {
            available = _dungeonRooms.Values
                .Where(room => room.ChannelId == session.ChannelId
                               && !room.Started
                               && !room.HasPendingTransition)
                .Where(room => room.CreateRequestPayload[35] == 0)
                .Where(room => room.Episode == requestedEpisode
                               && room.Dungeon == requestedDungeon
                               && room.Stage == requestedStage
                               && room.Difficulty == requestedDifficulty)
                .Where(room => room.Members.Count < 3)
                .OrderBy(room => room.Id)
                .FirstOrDefault();
        }
        if (available is not null)
            return TryJoinDungeonRoom(session, available.Id, out var joined) ? joined : null;

        var createPayload = new byte[44];
        if (session.Character is not null)
            WriteFixedGbk(createPayload.AsSpan(0, 24), session.Character.Name);
        // sub_6E99E0 always writes 100 to the CF6C word at +24. The selection
        // difficulty is the independent +30 word populated from +0x1E528.
        BinaryPrimitives.WriteUInt16LittleEndian(createPayload.AsSpan(24, 2), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(createPayload.AsSpan(30, 2),
            requestedDifficulty);
        createPayload[27] = requestedEpisode;
        createPayload[28] = requestedDungeon;
        createPayload[29] = requestedStage;
        created = true;
        return CreateDungeonRoom(session, createPayload);
    }

    private bool TryReuseDungeonTransitionQuickEntry(
        ConnectionSession member,
        ReadOnlySpan<byte> requestPayload,
        out DungeonRoom? room,
        out string? rejection)
    {
        lock (_dungeonRoomGate)
        {
            room = null;
            rejection = null;
            if (!_dungeonRooms.TryGetValue(member.DungeonRoomId, out var retainedRoom)
                || !retainedRoom.HasPendingTransition
                || !retainedRoom.TransitioningSessionIds.Contains(member.SessionId))
                return false;

            if (!retainedRoom.Members.ContainsKey(member.SessionId)
                || retainedRoom.OwnerSessionId == member.SessionId)
            {
                rejection = "requester is not a retained non-owner member";
                return false;
            }

            if (!retainedRoom.TransitionDisconnectedSessionIds.Contains(member.SessionId)
                || !retainedRoom.TransitionGameConnectedSessionIds.Contains(member.SessionId))
            {
                rejection = "retail CF1D and CF09/CF0A transition handshakes are incomplete";
                return false;
            }

            var mode = BinaryPrimitives.ReadUInt16LittleEndian(requestPayload.Slice(0, 2));
            if (mode == 20
                || requestPayload[2] != retainedRoom.PendingEpisode
                || !IsRetainedDungeonStageSelectorAccepted(retainedRoom, requestPayload[3])
                || requestPayload[4] != retainedRoom.PendingDungeon
                || !IsRetainedDungeonDifficultySelectorAccepted(
                    retainedRoom,
                    requestPayload[5]))
            {
                var normalDifficulty = EncodeDungeonDifficultySelector(
                    retainedRoom.PendingLogicalDifficulty,
                    superBoss: false);
                rejection =
                    $"selectors={mode}/{requestPayload[2]}/{requestPayload[3]}/{requestPayload[4]}/{requestPayload[5]} expectedTarget=10-or-100/{retainedRoom.PendingEpisode}/{retainedRoom.PendingStage}/{retainedRoom.PendingDungeon}/{retainedRoom.Difficulty} sourceNormalDifficulty={normalDifficulty}";
                return false;
            }

            retainedRoom.TransitionReenteredSessionIds.Add(member.SessionId);
            room = retainedRoom;
            return true;
        }
    }

    private static bool IsRetainedDungeonStageSelectorAccepted(
        DungeonRoom room,
        byte requestedStage)
        => IsRetainedDungeonBossReentry(room)
            ? requestedStage == 0
            : requestedStage == room.PendingStage;

    private static bool IsRetainedDungeonDifficultySelectorAccepted(
        DungeonRoom room,
        ushort requestedDifficulty)
        => IsRetainedDungeonBossReentry(room)
            ? requestedDifficulty == EncodeDungeonDifficultySelector(
                room.PendingLogicalDifficulty,
                superBoss: false)
            : requestedDifficulty == room.Difficulty;

    private static bool IsRetainedDungeonBossReentry(DungeonRoom room)
        => room.PendingStage == 1
           && room.SettlementAction is DungeonSettlementAction.ChallengeBoss
               or DungeonSettlementAction.RetryCurrent;

    private async Task<IReadOnlyDictionary<long, byte>> LoadDungeonReadyRoomRanksAsync(
        DungeonRoom room,
        CancellationToken token)
    {
        byte hdIndex;
        byte episode;
        byte dungeon;
        byte stage;
        byte logicalDifficulty;
        CharacterRecord[] characters;
        lock (_dungeonRoomGate)
        {
            hdIndex = room.HdIndex;
            episode = room.BattleEpisode;
            dungeon = room.BattleDungeon;
            stage = room.BattleStage;
            logicalDifficulty = room.BattleLogicalDifficulty;
            characters = room.Members.Values
                .Select(member => member.Session.Character)
                .Where(character => character is not null)
                .Cast<CharacterRecord>()
                .ToArray();
        }

        var archiveSlot = dungeon + stage;
        if (archiveSlot > 3 || logicalDifficulty >= DungeonDifficultyCount)
            return new Dictionary<long, byte>();

        var result = new Dictionary<long, byte>(characters.Length);
        foreach (var character in characters)
        {
            byte packedRatings;
            if (hdIndex == 0)
            {
                if (episode >= DungeonEpisodeCount)
                    continue;
                var ratings = await _database.GetDungeonBestRatingsAsync(character.Id, token);
                packedRatings = ratings[episode * DungeonDifficultyCount + logicalDifficulty];
            }
            else
            {
                if (hdIndex != 1 || episode >= 4)
                    continue;
                var ratings = await _database.GetDungeonSecretBestRatingsAsync(character.Id, token);
                packedRatings = ratings[episode];
            }
            result[character.Id] = ExtractPackedDungeonReadyRoomRank(
                packedRatings, dungeon, stage);
        }
        return result;
    }

    private void QueueDungeonMemberSnapshots(
        ConnectionSession initialized,
        IReadOnlyDictionary<long, byte> readyRoomRanks)
    {
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(initialized.DungeonRoomId, out var room)
                || initialized.Character is null
                || !room.Members.ContainsKey(initialized.SessionId)
                || !room.Members.TryGetValue(room.OwnerSessionId, out var ownerMember)
                || ownerMember.Session.Character is null
                || !_activeWorldSessions.TryGetValue(initialized.SessionId, out var initializedPresence))
                return;

            room.EntityInitializedSessions.Add(initialized.SessionId);
            initialized.PendingBroadcasts.Add(new PendingNativeBroadcast(
                initializedPresence,
                0xCF7E,
                BuildGameReadyPayload(
                    initialized.Character,
                    initialized.DungeonReady,
                    initialized.DungeonTeamCode),
                "local dungeon ready/team snapshot"));
            var ownerCharacter = ownerMember.Session.Character;
            foreach (var member in room.Members.Values)
            {
                var peer = member.Session;
                if (peer.SessionId == initialized.SessionId
                    || peer.Character is null
                    || !room.EntityInitializedSessions.Contains(peer.SessionId))
                    continue;

                QueueDungeonEntityAnnouncementLocked(
                    room, initialized, initialized, peer, ownerCharacter, readyRoomRanks,
                    "existing dungeon member snapshot");
                QueueDungeonEntityAnnouncementLocked(
                    room, initialized, peer, initialized, ownerCharacter, readyRoomRanks,
                    "dungeon member entered");
            }
        }
    }

    private void QueueDungeonEntityAnnouncementLocked(
        DungeonRoom room,
        ConnectionSession source,
        ConnectionSession recipient,
        ConnectionSession entity,
        CharacterRecord ownerCharacter,
        IReadOnlyDictionary<long, byte> readyRoomRanks,
        string reason)
    {
        var announcementKey = $"{recipient.SessionId}\0{entity.SessionId}";
        if (!room.EntityAnnouncements.Add(announcementKey)
            || entity.Character is null
            || !_activeWorldSessions.TryGetValue(recipient.SessionId, out var recipientPresence))
            return;

        source.PendingBroadcasts.Add(new PendingNativeBroadcast(
            recipientPresence,
            0xCF71,
            BuildGameRoomUserPayload(
                entity.Character, ownerCharacter, entity.DungeonSlotIndex,
                readyRoomRank: readyRoomRanks.GetValueOrDefault(entity.Character.Id)),
            reason));
        if (entity.DungeonMulticastInitialized)
        {
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                recipientPresence,
                0xCFDA,
                BuildMulticastingGameEventPayload(entity.Character),
                "existing dungeon member entity activation"));
        }
        if (entity.DungeonReady)
        {
            source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                recipientPresence,
                0xCF7E,
                BuildGameReadyPayload(entity.Character, true, entity.DungeonTeamCode),
                "existing dungeon ready snapshot"));
        }
    }

    private ConnectionSession? FindDungeonRoomOwner(ConnectionSession requester)
    {
        lock (_dungeonRoomGate)
        {
            return _dungeonRooms.TryGetValue(requester.DungeonRoomId, out var room)
                   && room.Members.TryGetValue(room.OwnerSessionId, out var owner)
                ? owner.Session
                : null;
        }
    }

    private CharacterRecord? FindDungeonRoomOwnerAfterLeave(ConnectionSession leaving)
    {
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(leaving.DungeonRoomId, out var room))
                return null;
            if (room.OwnerSessionId != leaving.SessionId)
                return room.Members.GetValueOrDefault(room.OwnerSessionId)?.Session.Character;
            return room.Members.Values
                .Where(member => member.Session.SessionId != leaving.SessionId)
                .OrderBy(member => member.SlotIndex)
                .Select(member => member.Session.Character)
                .FirstOrDefault(character => character is not null);
        }
    }

    private ConnectionSession? FindDungeonRoomMember(ConnectionSession requester, uint cursor)
    {
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(requester.DungeonRoomId, out var room)
                || !room.Members.TryGetValue(requester.SessionId, out var requesterMember))
                return null;
            // CF70 initializes the caller's local HUD/entity. Its cursor is an
            // opaque client object value, not a character id or list index.
            _ = cursor;
            return requesterMember.Session;
        }
    }

    private static uint[] BuildDungeonCollisionScoresLocked(DungeonRoom room)
    {
        var scores = new uint[3];
        foreach (var member in room.Members.Values)
        {
            if (member.SlotIndex >= scores.Length || member.Session.Character is not { } character)
                continue;
            scores[member.SlotIndex] = (uint)Math.Clamp(
                room.HitScores.GetValueOrDefault(character.Id), 0, int.MaxValue);
        }
        return scores;
    }

    private bool IsDungeonEntityInRoom(ConnectionSession source, uint entityUid)
    {
        if (entityUid == 0 || entityUid > ushort.MaxValue)
            return false;
        lock (_dungeonRoomGate)
        {
            return _dungeonRooms.TryGetValue(source.DungeonRoomId, out var room)
                   && room.Members.ContainsKey(source.SessionId)
                   && room.Members.Values.Any(member =>
                       member.Session.Character is { } character
                       && GetSceneEntityId(character) == entityUid);
        }
    }

    private bool TrySetDungeonSlotState(ConnectionSession requester, ushort slotIndex, byte state)
    {
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(requester.DungeonRoomId, out var room)
                || room.Started
                || room.OwnerSessionId != requester.SessionId
                || slotIndex >= room.SlotStates.Length)
                return false;

            var occupiedByOther = room.Members.Values.Any(member =>
                member.SlotIndex == slotIndex
                && member.Session.SessionId != requester.SessionId);
            if (occupiedByOther)
                return false;

            room.SlotStates[slotIndex] = state;
            foreach (var member in room.Members.Values)
                room.SlotStates.CopyTo(member.Session.DungeonSlotStates, 0);
            return true;
        }
    }

    private bool TrySetDungeonReady(ConnectionSession requester, bool ready, byte teamCode)
    {
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(requester.DungeonRoomId, out var room)
                || room.Started
                || !room.Members.ContainsKey(requester.SessionId))
                return false;
            requester.DungeonReady = ready;
            requester.DungeonTeamCode = teamCode;
            return true;
        }
    }

    private bool CanStartDungeonRoom(ConnectionSession requester)
    {
        lock (_dungeonRoomGate)
        {
            return _dungeonRooms.TryGetValue(requester.DungeonRoomId, out var room)
                   && room.OwnerSessionId == requester.SessionId
                   && !room.Started
                   && room.Members.Values
                       .Where(member => member.Session.SessionId != room.OwnerSessionId)
                       .All(member => member.Session.DungeonReady);
        }
    }

    private async Task<bool> CanCharacterEnterDungeonSelectionAsync(
        CharacterRecord character,
        byte episode,
        byte dungeon,
        byte difficulty,
        CancellationToken token)
    {
        var clearMasks = await _database.GetDungeonClearMasksAsync(character.Id, token);
        return IsDungeonSelectionOpen(
            clearMasks,
            character.Level,
            episode,
            dungeon,
            difficulty);
    }

    private async Task<bool> CanDungeonRoomMembersEnterSelectionAsync(
        DungeonRoom room,
        byte episode,
        byte dungeon,
        byte difficulty,
        CancellationToken token)
    {
        (string SessionId, CharacterRecord Character)[] members;
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(room.Id, out var current)
                || !ReferenceEquals(current, room))
                return false;
            members = room.Members.Values
                .Where(member => member.Session.Character is not null)
                .Select(member => (member.Session.SessionId, member.Session.Character!))
                .ToArray();
            if (members.Length != room.Members.Count)
                return false;
        }

        foreach (var member in members)
        {
            if (!await CanCharacterEnterDungeonSelectionAsync(
                    member.Character,
                    episode,
                    dungeon,
                    difficulty,
                    token))
                return false;
        }
        // The SQLite reads above intentionally run outside the room lock. The
        // exact membership must still match the checked snapshot when they
        // finish; otherwise a newly joined-and-readied member could bypass the
        // retail per-character progress gate before CF7F.
        lock (_dungeonRoomGate)
        {
            return _dungeonRooms.TryGetValue(room.Id, out var current)
                   && ReferenceEquals(current, room)
                   && room.Members.Count == members.Length
                   && members.All(member =>
                       room.Members.TryGetValue(member.SessionId, out var currentMember)
                       && currentMember.Session.Character?.Id == member.Character.Id);
        }
    }

    private static bool IsDungeonSelectionOpen(
        byte[] clearMasks,
        int playerLevel,
        byte episode,
        byte dungeon,
        byte difficulty)
    {
        if (episode >= DungeonEpisodeCount
            || dungeon >= DungeonCountPerEpisode
            || difficulty >= DungeonDifficultyCount)
            return false;

        // Retail sub_509BC0 indexes C355 by episode and difficulty, then tests
        // the dungeon bit inside that byte. Dungeon 3 is complete only after
        // both its normal bit 2 and hidden Super-BOSS bit 3 are present.
        if (dungeon == 0)
            return true;
        var maskIndex = episode * DungeonDifficultyCount + difficulty;
        if (maskIndex < clearMasks.Length
            && IsClientDungeonCleared(clearMasks[maskIndex], dungeon - 1))
            return true;
        return episode < DungeonLevelFallbackEpisodeCount
               && playerLevel >= (episode + 1) * 5;
    }

    private bool IsStartedDungeonRoomMember(ConnectionSession requester)
    {
        lock (_dungeonRoomGate)
        {
            return _dungeonRooms.TryGetValue(requester.DungeonRoomId, out var room)
                   && room.Started
                   && room.Members.ContainsKey(requester.SessionId);
        }
    }

    private (int ExpectedPeerCount, P2PPeerEndpoint[] Peers) GetDungeonP2PPeers(
        ConnectionSession requester)
    {
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(requester.DungeonRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId))
                return (0, []);

            var otherMembers = room.Members.Values
                .Where(member => member.Session.SessionId != requester.SessionId)
                .OrderBy(member => member.SlotIndex)
                .ToArray();
            var peers = otherMembers
                .Where(member => member.Session.Character is not null
                                 && member.Session.P2PInfoRegistered
                                 && member.Session.P2PIpAddress is not null
                                 && member.Session.P2PPort != 0)
                .Select(member => new P2PPeerEndpoint(
                    member.SlotIndex,
                    (ushort)Math.Clamp(
                        member.Session.Character!.Id,
                        1L,
                        (long)ushort.MaxValue),
                    member.Session.P2PIpAddress!,
                    member.Session.P2PPort))
                .ToArray();
            return (otherMembers.Length, peers);
        }
    }

    private void MarkDungeonRoomStarted(ConnectionSession requester)
    {
        lock (_dungeonRoomGate)
            if (_dungeonRooms.TryGetValue(requester.DungeonRoomId, out var room))
            {
                if (room.HasPendingTransition)
                {
                    room.BattleEpisode = room.PendingEpisode;
                    room.BattleDungeon = room.PendingDungeon;
                    room.BattleStage = room.PendingStage;
                    room.BattleLogicalDifficulty = room.PendingLogicalDifficulty;
                    room.CreateRequestPayload[27] = room.BattleEpisode;
                    room.CreateRequestPayload[28] = room.BattleDungeon;
                    room.CreateRequestPayload[29] = room.BattleStage;
                    room.HasPendingTransition = false;
                }
                room.TransitioningSessionIds.Clear();
                room.TransitionDisconnectedSessionIds.Clear();
                room.TransitionGameConnectedSessionIds.Clear();
                room.TransitionReenteredSessionIds.Clear();
                room.TransitionTownReentrySentSessionIds.Clear();
                room.TransitionConnectionReentrySentSessionIds.Clear();
                room.TransitionStartedUtc = null;
                room.SettlementAction = DungeonSettlementAction.None;
                ResetDungeonBattleLocked(room, beginTransition: false);
            }
    }

    private bool TryGetDungeonTransition(
        ConnectionSession member,
        out int roomId,
        out DungeonSettlementAction action)
    {
        lock (_dungeonRoomGate)
        {
            roomId = member.DungeonRoomId;
            action = DungeonSettlementAction.None;
            if (!_dungeonRooms.TryGetValue(member.DungeonRoomId, out var room)
                || !room.Members.ContainsKey(member.SessionId)
                || !room.TransitioningSessionIds.Contains(member.SessionId)
                || room.TransitionStartedUtc is null
                || room.SettlementAction is not (DungeonSettlementAction.RetryCurrent
                    or DungeonSettlementAction.NextDungeon
                    or DungeonSettlementAction.ChallengeBoss
                    or DungeonSettlementAction.NextDifficulty))
                return false;

            action = room.SettlementAction;
            return true;
        }
    }

    private bool TryBuildDungeonTransitionReentry(
        ConnectionSession member,
        DungeonTransitionReentryTrigger trigger,
        out ushort opcode,
        out byte[] payload,
        out string role,
        out bool alreadySent)
    {
        lock (_dungeonRoomGate)
        {
            opcode = 0;
            payload = [];
            role = string.Empty;
            alreadySent = false;
            if (!_dungeonRooms.TryGetValue(member.DungeonRoomId, out var room)
                || !room.HasPendingTransition
                || !room.TransitioningSessionIds.Contains(member.SessionId)
                || !room.Members.ContainsKey(member.SessionId)
                || room.Members.GetValueOrDefault(room.OwnerSessionId)?.Session.Character is not { } owner)
                return false;

            var sentSessions = trigger == DungeonTransitionReentryTrigger.TownPage
                ? room.TransitionTownReentrySentSessionIds
                : room.TransitionConnectionReentrySentSessionIds;
            if (!sentSessions.Add(member.SessionId))
            {
                alreadySent = true;
                return false;
            }

            if (room.OwnerSessionId == member.SessionId)
            {
                opcode = 0xCF6D;
                payload = DungeonProtocol.BuildCreateResponse(
                    checked((ushort)room.Id),
                    owner.Id,
                    owner.Name,
                    DecodeDungeonPassword(room),
                    room.SlotStates);
                role = "owner";
                return true;
            }

            opcode = 0xCF76;
            payload = DungeonProtocol.BuildEnterResponse(
                checked((ushort)room.Id),
                owner.Id,
                owner.Name,
                DecodeDungeonTitle(room),
                DecodeDungeonPassword(room),
                DecodeDungeonDifficulty(room),
                10);
            role = "member";
            return true;
        }
    }

    private void MarkDungeonTransitionDisconnected(ConnectionSession member)
    {
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(member.DungeonRoomId, out var room))
                return;
            room.TransitionDisconnectedSessionIds.Add(member.SessionId);
            CompleteDungeonTransitionIfReenteredLocked(room, member.SessionId);
        }
    }

    private void MarkDungeonTransitionGameConnected(ConnectionSession member)
    {
        lock (_dungeonRoomGate)
        {
            if (_dungeonRooms.TryGetValue(member.DungeonRoomId, out var room)
                && room.TransitioningSessionIds.Contains(member.SessionId)
                && room.TransitionDisconnectedSessionIds.Contains(member.SessionId))
                room.TransitionGameConnectedSessionIds.Add(member.SessionId);
        }
    }

    private bool MarkDungeonTransitionReentered(
        ConnectionSession member,
        bool allowDirectGameData = false)
    {
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(member.DungeonRoomId, out var room)
                || !room.TransitioningSessionIds.Contains(member.SessionId)
                || !room.Members.ContainsKey(member.SessionId))
                return false;

            // The retail client sends C587/CF70 immediately after CF8C and
            // needs that room refresh before it can advance to CF73/CF1D.
            // It is only a provisional reentry: the pending dungeon remains
            // authoritative until CF7F starts and commits the next battle.
            _ = allowDirectGameData;
            room.TransitionReenteredSessionIds.Add(member.SessionId);
            CompleteDungeonTransitionIfReenteredLocked(room, member.SessionId);
            return true;
        }
    }

    private static void CompleteDungeonTransitionIfReenteredLocked(
        DungeonRoom room,
        string sessionId)
    {
        if (!room.TransitionReenteredSessionIds.Contains(sessionId))
            return;
        // CF8B transitions must survive the client's intermediate disconnect
        // and town-page bridge. CF7F is the sole commit point for Pending*,
        // and MarkDungeonRoomStarted clears these transport markers there.
        if (room.HasPendingTransition)
            return;
        room.TransitionDisconnectedSessionIds.Remove(sessionId);
        room.TransitionGameConnectedSessionIds.Remove(sessionId);
        room.TransitionReenteredSessionIds.Remove(sessionId);
        room.TransitioningSessionIds.Remove(sessionId);
        if (room.TransitioningSessionIds.Count != 0)
            return;
        room.TransitionDisconnectedSessionIds.Clear();
        room.TransitionGameConnectedSessionIds.Clear();
        room.TransitionReenteredSessionIds.Clear();
        room.TransitionTownReentrySentSessionIds.Clear();
        room.TransitionConnectionReentrySentSessionIds.Clear();
        room.TransitionStartedUtc = null;
        room.SettlementAction = DungeonSettlementAction.None;
    }

    private async Task RestoreDungeonVitalsAsync(
        ConnectionSession requester,
        bool entireRoom,
        CancellationToken token)
    {
        ConnectionSession[] targets;
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(requester.DungeonRoomId, out var room))
                targets = [requester];
            else
                targets = entireRoom
                    ? room.Members.Values.Select(member => member.Session).ToArray()
                    : [requester];

            foreach (var target in targets)
            {
                if (target.Character is not { } character)
                    continue;
                character.CurrentHp = Math.Max(1, character.MaxHp);
                character.CurrentMp = Math.Max(1, character.MaxMp);
            }
        }

        foreach (var target in targets)
        {
            if (target.Character is not { } character || target.AccountId <= 0)
                continue;
            await _database.SaveCharacterRuntimeStateAsync(
                target.AccountId,
                character.Id,
                target.SessionId,
                CreateRuntimeState(character, target.ChannelId),
                token);
        }
    }

    private void QueueUserAutoHealing(
        ConnectionSession source,
        ConnectionSession targetSession,
        string reason)
    {
        if (targetSession.Character is not { } character
            || !_activeWorldSessions.TryGetValue(targetSession.SessionId, out var target))
            return;
        source.PendingBroadcasts.Add(new PendingNativeBroadcast(
            target,
            0xD8FF,
            BuildUserHpMpAutoHealingPayload(character),
            reason));
    }

    private void QueueDungeonAutoHealing(ConnectionSession source, string reason)
    {
        ConnectionSession[] targets;
        lock (_dungeonRoomGate)
        {
            targets = _dungeonRooms.TryGetValue(source.DungeonRoomId, out var room)
                ? room.Members.Values.Select(member => member.Session).ToArray()
                : [source];
        }
        foreach (var target in targets)
            QueueUserAutoHealing(source, target, reason);
    }

    private static void ApplyDungeonUpgradePickupRecovery(
        CharacterRecord character,
        byte subtype,
        int partySize)
    {
        if (subtype == 2)
        {
            var restored = partySize > 1
                ? (int)(Math.Max(0, character.MaxHp) * 0.3d)
                : 300;
            character.CurrentHp = Math.Min(
                Math.Max(0, character.MaxHp),
                (int)Math.Min(int.MaxValue, (long)Math.Max(0, character.CurrentHp) + restored));
        }
        else if (subtype == 3)
        {
            var restored = partySize > 1
                ? (int)(Math.Max(0, character.MaxMp) * 0.3d)
                : 30;
            character.CurrentMp = Math.Min(
                Math.Max(0, character.MaxMp),
                (int)Math.Min(int.MaxValue, (long)Math.Max(0, character.CurrentMp) + restored));
        }
    }

    private static bool TryApplyDungeonReportedMp(
        CharacterRecord character,
        ushort reportedCurrentMp)
    {
        if (reportedCurrentMp <= character.CurrentMp
            || reportedCurrentMp > Math.Max(0, character.MaxMp))
            return false;
        character.CurrentMp = reportedCurrentMp;
        return true;
    }

    private static bool HasPendingDungeonRewardsLocked(DungeonRoom room)
    {
        if (!room.Battle.SettlementStarted
            && !room.Battle.Bosses.Values.Any(boss => boss.ClearAnnounced))
            return false;
        return room.Members.Values.Any(member =>
            member.Session.OnlineTracked
            && member.Session.AccountId > 0
            && member.Session.Character is { } character
            && !room.Battle.RewardedCharacters.Contains(character.Id));
    }

    private static void ResetDungeonBattleLocked(DungeonRoom room, bool beginTransition)
    {
        // A CF8B stage transition returns the retail client to the dungeon
        // room flow, where it loads CFEC and sends CF7F again. Marking that
        // room as already started makes CanStartDungeonRoom reject CF7F and
        // leaves the client on its loading overlay. A direct CF7F start uses
        // beginTransition=false and enters the active battle state.
        if (beginTransition)
        {
            var episode = room.HasPendingTransition ? room.PendingEpisode : room.BattleEpisode;
            var dungeon = room.HasPendingTransition ? room.PendingDungeon : room.BattleDungeon;
            var stage = room.HasPendingTransition ? room.PendingStage : room.BattleStage;
            var logicalDifficulty = room.HasPendingTransition
                ? room.PendingLogicalDifficulty
                : room.BattleLogicalDifficulty;
            room.Battle.State = DungeonBattleState.Settled;
            room.Battle = new DungeonBattleInstance(
                room.HdIndex,
                episode,
                dungeon,
                stage,
                logicalDifficulty);
            foreach (var member in room.Members.Values)
            {
                member.Session.DungeonMulticastInitialized = false;
                ResetP2PState(member.Session);
            }
            return;
        }

        if (room.Battle.Episode != room.BattleEpisode
            || room.Battle.Dungeon != room.BattleDungeon
            || room.Battle.Stage != room.BattleStage)
        {
            room.Battle.State = DungeonBattleState.Settled;
            room.Battle = new DungeonBattleInstance(
                room.HdIndex,
                room.BattleEpisode,
                room.BattleDungeon,
                room.BattleStage,
                room.BattleLogicalDifficulty);
        }

        // CFEC prepares map/card data before CF7F. Activating that same
        // instance must retain the preparation while resetting runtime-only
        // combat and settlement state.
        room.HitScores.Clear();
        room.BossBonusScores.Clear();
        room.Npcs.Clear();
        room.Battle.DefeatedUncataloguedRuntimeUids.Clear();
        room.Bosses.Clear();
        room.RewardedCharacters.Clear();
        room.Battle.RewardCommittingCharacters.Clear();
        room.EndGamePayloads.Clear();
        room.Battle.SettlementStarted = false;
        room.Battle.DeadCharacters.Clear();
        room.Battle.RemainingContinues.Clear();
        room.Battle.ContinuingCharacters.Clear();
        room.Battle.ActiveSkills.Clear();
        room.Battle.SkillCooldowns.Clear();
        room.Battle.ParticipantCharacterIds.Clear();
        foreach (var member in room.Members.Values)
        {
            if (member.Session.Character is { } character)
            {
                room.Battle.ParticipantCharacterIds.Add(character.Id);
                room.Battle.RemainingContinues[character.Id] = DungeonInitialContinueCount;
            }
            member.Session.HasReportedDungeonPosition = false;
        }
        room.Battle.PartySizeAtStart = room.Battle.ParticipantCharacterIds.Count;
        if (room.Battle.PartySizeAtStart is < 1 or > 3
            || room.Battle.PartySizeAtStart != room.Members.Count)
            throw new InvalidOperationException("Dungeon battle participant snapshot is inconsistent with the room.");
        room.ClaimedDrops.Clear();
        room.GeneratedDrops.Clear();
        room.Battle.LastBossResourceUid = null;
        room.Battle.NextGeneratedDropToken = 1;
        // CFEB normally selects the shared schedule before CF7F. Keep CF7F
        // authoritative as well so packet ordering can never activate a
        // battle whose collision handlers have no map/runtime namespace.
        var mapIndex = GetOrCreateDungeonMapIndexLocked(room);
        foreach (var boss in DungeonCombatCatalog.GetInitiallyScheduledBosses(
                     room.HdIndex,
                     room.BattleEpisode,
                     room.BattleDungeon,
                     room.BattleStage,
                     mapIndex))
            room.Bosses.Add(boss.Key, new DungeonBossState(boss.Value));
        if (room.Bosses.Count == 1)
            room.Battle.LastBossResourceUid = room.Bosses.Keys.Single();
        room.Started = true;
    }

    private static bool TryGetDungeonContinueCost(
        byte hdIndex,
        byte episode,
        out ushort cost)
    {
        var table = hdIndex switch
        {
            0 => NormalDungeonContinueCosts,
            1 => SpecialDungeonContinueCosts,
            _ => null
        };
        if (table is null || episode >= table.Length)
        {
            cost = 0;
            return false;
        }

        cost = table[episode];
        return true;
    }

    private static byte[] GetOrCreateDungeonUpgradeDropPlanLocked(
        DungeonRoom room)
    {
        return room.UpgradeDropSubtypes ??= DungeonProtocol.CreateUpgradeDropPlan();
    }

    private static byte[] GetOrCreateDungeonItemDropPlanLocked(
        DungeonRoom room)
    {
        return room.InDungeonItemDropCodes ??= DungeonProtocol.CreateInDungeonItemDropPlan();
    }

    private static (byte[] First, byte[] Second) GetOrCreateDungeonRandomPlansLocked(
        DungeonRoom room)
    {
        room.Battle.RandomPercentPlanA ??= DungeonProtocol.CreateRandomPercentPlan();
        room.Battle.RandomPercentPlanB ??= DungeonProtocol.CreateRandomPercentPlan();
        return (room.Battle.RandomPercentPlanA, room.Battle.RandomPercentPlanB);
    }

    private static ushort GetOrCreateDungeonMapIndexLocked(DungeonRoom room)
    {
        if (room.SelectedMapIndex is { } selected)
            return selected;

        // Every retail dungeon .sstg has nine schedule slots. Across all
        // regular resources they are laid out as three L/M/H groups, one for
        // each supported room population, with three equivalent _00/_01/_02
        // layouts per group. MapIdx is adapter-authoritative and must be chosen
        // once per battle instance so every member loads the same schedule.
        var populationGroup = Math.Clamp(room.Members.Count, 1, 3) - 1;
        // The solo _00/_01 schedules do not run the BOSS attack/component
        // lifecycle correctly in the retail client. _02 is the complete solo
        // schedule; populated M/H groups keep their three official variants.
        var layoutVariant = room.Members.Count == 1
            ? 2
            : RandomNumberGenerator.GetInt32(3);
        selected = checked((ushort)(populationGroup * 3 + layoutVariant));
        room.SelectedMapIndex = selected;
        return selected;
    }

    private static void InvalidatePendingDungeonGameDataLocked(DungeonRoom room)
    {
        if (room.Started)
            return;
        // CFEC selects one shared MapIdx and two random tables for the target
        // battle instance. Once any remaining member has received that data,
        // a transition-time leave must not make later members load a different
        // schedule. RemoveDungeonRoomMemberLocked removes the leaver's cache
        // before this check, so an unobserved target instance can still be
        // regenerated for its new population.
        if (room.HasPendingTransition && room.GameDataPayloadsBySession.Count != 0)
            return;
        room.SelectedMapIndex = null;
        room.UpgradeDropSubtypes = null;
        room.InDungeonItemDropCodes = null;
        room.Battle.RandomPercentPlanA = null;
        room.Battle.RandomPercentPlanB = null;
        room.GameDataPayloadsBySession.Clear();
    }

    private bool TryClaimDungeonDrop(
        DungeonRoom room,
        ushort pickupType,
        ushort dropUid)
    {
        lock (_dungeonRoomGate)
            return room.ClaimedDrops.Add((pickupType, dropUid));
    }

    private static ushort RegisterGeneratedDungeonCardLocked(
        DungeonRoom room,
        uint cardCode)
    {
        for (var attempt = 0; attempt < ushort.MaxValue; attempt++)
        {
            var token = room.Battle.NextGeneratedDropToken++;
            if (token == 0)
                continue;
            if (room.GeneratedDrops.TryAdd(token, cardCode))
                return token;
        }

        throw new InvalidOperationException("The dungeon generated-card token space is exhausted.");
    }

    private void QueueDungeonBroadcast(
        ConnectionSession source,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        bool includeSource,
        string reason)
    {
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(source.DungeonRoomId, out var room))
                return;
            foreach (var member in room.Members.Values)
            {
                if (!includeSource && member.Session.SessionId == source.SessionId)
                    continue;
                if (_activeWorldSessions.TryGetValue(member.Session.SessionId, out var target))
                {
                    source.PendingBroadcasts.Add(new PendingNativeBroadcast(
                        target,
                        opcode,
                        payload.ToArray(),
                        reason));
                }
            }
        }
    }

    private async Task QueueDungeonStartAsync(ConnectionSession owner, CancellationToken token)
    {
        var skillRoom = GetDungeonRoom(owner);
        if (skillRoom is null)
            return;
        var gameSkillRecords = await GetDungeonGameSkillRecordsAsync(skillRoom, token);
        List<(ConnectionSession Session, WorldPresence Target, byte[] GameDataPayload)> targets = [];
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(owner.DungeonRoomId, out var room))
                return;
            foreach (var member in room.Members.Values)
            {
                var targetSession = member.Session;
                if (targetSession.SessionId == owner.SessionId || targetSession.Character is null)
                    continue;
                if (!_activeWorldSessions.TryGetValue(targetSession.SessionId, out var target))
                    continue;
                if (!room.GameDataPayloadsBySession.TryGetValue(
                        targetSession.SessionId, out var gameDataPayload))
                {
                    ushort stageIndex = 0;
                    ushort showStageNumber = 0;
                    if (room.GameDataPayloadsBySession.TryGetValue(
                            owner.SessionId, out var ownerGameDataPayload)
                        && ownerGameDataPayload.Length >= 0x2D6)
                    {
                        stageIndex = ownerGameDataPayload[0x2D2];
                        showStageNumber = ownerGameDataPayload[0x2D3];
                    }
                    var mapIndex = GetOrCreateDungeonMapIndexLocked(room);
                    var upgradeDropPlan = GetOrCreateDungeonUpgradeDropPlanLocked(room);
                    var inDungeonItemDropPlan = GetOrCreateDungeonItemDropPlanLocked(room);
                    var (randomPlanA, randomPlanB) = GetOrCreateDungeonRandomPlansLocked(room);
                    gameDataPayload = DungeonProtocol.BuildGameData(
                        stageIndex,
                        showStageNumber,
                        mapIndex,
                        randomPlanA,
                        randomPlanB,
                        upgradeDropPlan,
                        inDungeonItemDropPlan);
                }
                DungeonProtocol.WriteGameSkillRecords(gameDataPayload, gameSkillRecords);
                room.GameDataPayloadsBySession[targetSession.SessionId] = gameDataPayload.ToArray();
                targets.Add((targetSession, target, gameDataPayload.ToArray()));
            }
        }

        foreach (var (_, target, gameDataPayload) in targets)
        {
            owner.PendingBroadcasts.Add(new PendingNativeBroadcast(
                target,
                0xCF80,
                [],
                "dungeon game start"));
            if (gameDataPayload.Length > 0)
            {
                owner.PendingBroadcasts.Add(new PendingNativeBroadcast(
                    target,
                    0xCFEC,
                    gameDataPayload,
                    "dungeon game data after start"));
            }
        }
    }

    private async Task<IReadOnlyList<DungeonProtocol.GameSkillRecord>>
        GetDungeonGameSkillRecordsAsync(DungeonRoom room, CancellationToken token)
    {
        DungeonMember[] members;
        lock (_dungeonRoomGate)
        {
            if (!_dungeonRooms.TryGetValue(room.Id, out var currentRoom)
                || !ReferenceEquals(currentRoom, room))
                return [];
            members = room.Members.Values
                .Where(member => member.Session.Character is not null)
                .OrderBy(member => member.SlotIndex)
                .Take(2)
                .ToArray();
        }

        var records = new List<DungeonProtocol.GameSkillRecord>(members.Length);
        foreach (var member in members)
        {
            var character = member.Session.Character;
            if (character is null)
                continue;
            var slots = await GetEffectiveDungeonSkillSlotsAsync(member.Session, token);
            records.Add(new DungeonProtocol.GameSkillRecord(
                GetSceneEntityId(character),
                slots.Grade0,
                slots.Grade1,
                slots.Skill0,
                slots.Skill1));
        }
        return records;
    }

    private async Task<(uint Skill0, byte Grade0, uint Skill1, byte Grade1)>
        GetEffectiveDungeonSkillSlotsAsync(ConnectionSession session, CancellationToken token)
    {
        if (session.Character is not { } character)
            return (0, 0, 0, 0);
        if (character.SelectedSkill0 == 0 && character.SelectedSkill1 == 0)
            return (0, 0, 0, 0);

        var learnedSkills = await _database.GetCharacterSkillsAsync(character.Id, token);
        var grades = learnedSkills
            .Where(skill => skill.Grade is >= 1 and <= 5 && SkillCatalog.TryGet(skill.SkillCode, out _))
            .ToDictionary(skill => skill.SkillCode, skill => skill.Grade);
        var skill0 = grades.ContainsKey(character.SelectedSkill0)
            ? character.SelectedSkill0
            : 0u;
        var expandedSkillSlotActive = SkillSlotExpansionTime.TryDecode(
                character.SkillSlotExpansionExpires, out var skillSlotExpiration)
            && skillSlotExpiration > DateTime.Now;
        var skill1 = expandedSkillSlotActive
                     && grades.ContainsKey(character.SelectedSkill1)
                     && character.SelectedSkill1 != skill0
            ? character.SelectedSkill1
            : 0u;
        return (
            skill0,
            grades.GetValueOrDefault(skill0),
            skill1,
            grades.GetValueOrDefault(skill1));
    }

    private static long DungeonFramesToMilliseconds(ushort frames)
        => (frames * 1000L + DungeonLogicFramesPerSecond - 1)
           / DungeonLogicFramesPerSecond;

    private static int GetDungeonBaseAttackLocked(
        DungeonBattleInstance battle,
        long characterId,
        int normalBaseAttack,
        long now,
        out uint activeSkillCode,
        out byte activeSkillGrade)
    {
        if (battle.ActiveSkills.TryGetValue(characterId, out var activeSkill))
        {
            if (activeSkill.ActiveUntilTick > now)
            {
                activeSkillCode = activeSkill.SkillCode;
                activeSkillGrade = activeSkill.Grade;
                return Math.Max(1, (int)activeSkill.AttackValue);
            }
            battle.ActiveSkills.Remove(characterId);
        }

        activeSkillCode = 0;
        activeSkillGrade = 0;
        return Math.Max(1, normalBaseAttack);
    }

    private void RemoveDungeonRoomMember(ConnectionSession member)
    {
        lock (_dungeonRoomGate)
            RemoveDungeonRoomMemberLocked(member);
    }

    private void QueueDungeonDisconnectNotification(ConnectionSession member)
    {
        if (member.DungeonRoomId == 0 || member.Character is null)
            return;
        var owner = FindDungeonRoomOwnerAfterLeave(member) ?? member.Character;
        QueueDungeonBroadcast(
            member,
            0xCF74,
            BuildGameRoomLeavePayload(member.Character, owner),
            false,
            "dungeon connection leave");
    }

    private void RemoveDungeonRoomMemberLocked(ConnectionSession member)
    {
        if (!_dungeonRooms.TryGetValue(member.DungeonRoomId, out var room))
        {
            member.DungeonRoomId = 0;
            member.DungeonSlotIndex = 0;
            member.DungeonReady = false;
            member.DungeonTeamCode = 0;
            member.DungeonMulticastInitialized = false;
            ResetP2PState(member);
            return;
        }

        room.Members.Remove(member.SessionId);
        room.TransitioningSessionIds.Remove(member.SessionId);
        room.TransitionDisconnectedSessionIds.Remove(member.SessionId);
        room.TransitionGameConnectedSessionIds.Remove(member.SessionId);
        room.TransitionReenteredSessionIds.Remove(member.SessionId);
        room.TransitionTownReentrySentSessionIds.Remove(member.SessionId);
        room.TransitionConnectionReentrySentSessionIds.Remove(member.SessionId);
        if (room.TransitioningSessionIds.Count == 0)
        {
            room.TransitionDisconnectedSessionIds.Clear();
            room.TransitionGameConnectedSessionIds.Clear();
            room.TransitionReenteredSessionIds.Clear();
            room.TransitionTownReentrySentSessionIds.Clear();
            room.TransitionConnectionReentrySentSessionIds.Clear();
            room.TransitionStartedUtc = null;
            room.SettlementAction = DungeonSettlementAction.None;
        }
        room.EntityInitializedSessions.Remove(member.SessionId);
        room.EntityAnnouncements.RemoveWhere(value =>
            value.StartsWith(member.SessionId + "\0", StringComparison.Ordinal)
            || value.EndsWith("\0" + member.SessionId, StringComparison.Ordinal));
        room.GameDataPayloadsBySession.Remove(member.SessionId);
        if (room.Members.Count == 0)
        {
            _dungeonRooms.Remove(room.Id);
        }
        else
        {
            InvalidatePendingDungeonGameDataLocked(room);
            if (room.OwnerSessionId == member.SessionId)
            {
                var newOwner = room.Members.Values.OrderBy(value => value.SlotIndex).First();
                room.OwnerSessionId = newOwner.Session.SessionId;
                newOwner.Session.DungeonReady = false;
                newOwner.Session.DungeonTeamCode = 0;
            }
        }
        member.DungeonRoomId = 0;
        member.DungeonSlotIndex = 0;
        member.DungeonReady = false;
        member.DungeonTeamCode = 0;
        member.DungeonMulticastInitialized = false;
        ResetP2PState(member);
    }

    private static void ResetP2PState(ConnectionSession session)
    {
        session.P2PIpAddress = null;
        session.P2PPort = 0;
        session.P2PInfoRegistered = false;
        session.ArenaP2PProtocolConfirmed = false;
        session.HasReportedDungeonPosition = false;
    }

    private async Task FlushPendingBroadcastsAsync(
        ConnectionSession source,
        CancellationToken token)
    {
        var pending = source.PendingBroadcasts.ToArray();
        source.PendingBroadcasts.Clear();
        await SendNativeBroadcastBatchAsync(pending, token);

        var directPending = source.PendingSessionBroadcasts.ToArray();
        source.PendingSessionBroadcasts.Clear();
        await SendSessionBroadcastBatchAsync(directPending, token);
    }

    private Task SendNativeBroadcastBatchAsync(
        IReadOnlyCollection<PendingNativeBroadcast> notifications,
        CancellationToken token)
    {
        if (notifications.Count == 0)
            return Task.CompletedTask;
        return Task.WhenAll(notifications
            .GroupBy(item => item.Target.Session.SessionId, StringComparer.Ordinal)
            .Select(group => SendNativeBroadcastSequenceAsync(group, token)));
    }

    private async Task SendNativeBroadcastSequenceAsync(
        IEnumerable<PendingNativeBroadcast> notifications,
        CancellationToken token)
    {
        foreach (var notification in notifications)
            await SendNativeBroadcastAsync(notification, token);
    }

    private Task SendSessionBroadcastBatchAsync(
        IReadOnlyCollection<PendingSessionBroadcast> notifications,
        CancellationToken token)
    {
        if (notifications.Count == 0)
            return Task.CompletedTask;
        return Task.WhenAll(notifications
            .GroupBy(item => item.Target.SessionId, StringComparer.Ordinal)
            .Select(group => SendSessionBroadcastSequenceAsync(group, token)));
    }

    private async Task SendSessionBroadcastSequenceAsync(
        IEnumerable<PendingSessionBroadcast> notifications,
        CancellationToken token)
    {
        foreach (var notification in notifications)
            await SendSessionBroadcastAsync(notification, token);
    }

    private async Task SendNativeBroadcastAsync(
        PendingNativeBroadcast notification,
        CancellationToken token)
    {
        var target = notification.Target;
        var targetSession = target.Session;
        if (!targetSession.OnlineTracked || targetSession.Stream is null)
            return;

        try
        {
            var requestTemplate = new byte[8];
            requestTemplate[0] = NativeTransportTags[0];
            var frame = BuildNativeFrame(
                requestTemplate,
                notification.Opcode,
                notification.Payload,
                targetSession);
            await QueueOutboundWriteAsync(
                targetSession,
                new OutboundNativeWrite(
                    frame,
                    "WorldAdapter",
                    targetSession.ListenerPort,
                    target.RemoteIp,
                    true,
                    true,
                    notification.Reason),
                token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ChannelClosedException or IOException or SocketException or ObjectDisposedException)
        {
            _log($"WorldAdapter:{target.RemoteIp} 骞挎挱澶辫触 opcode=0x{notification.Opcode:X4} reason={notification.Reason}: {ex.Message}");
        }
    }

    private async Task SendSessionBroadcastAsync(
        PendingSessionBroadcast notification,
        CancellationToken token)
    {
        var target = notification.Target;
        if (!target.OnlineTracked || target.Stream is null)
            return;
        try
        {
            var requestTemplate = new byte[8];
            requestTemplate[0] = NativeTransportTags[0];
            var frame = BuildNativeFrame(
                requestTemplate,
                notification.Opcode,
                notification.Payload,
                target);
            await QueueOutboundWriteAsync(
                target,
                new OutboundNativeWrite(
                    frame,
                    "ArenaAdapter",
                    target.ListenerPort,
                    target.RemoteIp ?? "unknown",
                    true,
                    true,
                    notification.Reason),
                token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ChannelClosedException or IOException or SocketException or ObjectDisposedException)
        {
            _log($"ArenaAdapter:{target.RemoteIp} 骞挎挱澶辫触 opcode=0x{notification.Opcode:X4} reason={notification.Reason}: {ex.Message}");
        }
    }

    private async ValueTask QueueOutboundWriteAsync(
        ConnectionSession session,
        OutboundNativeWrite write,
        CancellationToken token)
    {
        var firstOpcode = ReadFirstNativeOpcode(write.Frames);
        if (write.IsBroadcast && IsHighFrequencyOpcode(firstOpcode))
        {
            if (session.OutboundWrites.Reader.CanCount
                && session.OutboundWrites.Reader.Count >= OutboundHighFrequencyLimit)
                return;
            session.OutboundWrites.Writer.TryWrite(write);
            return;
        }

        await session.OutboundWrites.Writer.WriteAsync(write, token);
    }

    private async Task RunOutboundWriterAsync(
        ConnectionSession session,
        NetworkStream stream,
        CancellationToken token)
    {
        try
        {
            await foreach (var write in session.OutboundWrites.Reader.ReadAllAsync(token))
            {
                if (write.UseTransportSequence)
                    FinalizeNativeFramesForSend(write.Frames, session);
                await stream.WriteAsync(write.Frames, token);
                TouchSessionActivity(session);
                LogOutboundWrite(write);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            session.DisconnectReason ??= $"下行发送失败：{ex.Message}";
            try { session.ConnectionCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private static void TouchSessionActivity(ConnectionSession session)
    {
        var now = DateTime.UtcNow;
        session.LastActivityUtc = now;
        try
        {
            // CancelAfter is reset on each activity. This is intentionally a
            // sliding idle timeout: an active client is not disconnected merely
            // because its TCP connection is older than the timeout window.
            session.ConnectionCancellation?.CancelAfter(TimeSpan.FromMinutes(30));
        }
        catch (ObjectDisposedException)
        {
            // Teardown won the race; the owning loop will finish the session.
        }
    }

    private void LogOutboundWrite(OutboundNativeWrite write)
    {
        foreach (var frame in SplitNativeFrames(write.Frames))
        {
            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6, 2));
            if (IsHighFrequencyOpcode(opcode))
                continue;
            var action = write.IsBroadcast ? "已广播原生帧" : "已发送原生响应";
            var reason = write.Reason is null ? string.Empty : $" reason={write.Reason}";
            _log($"{write.Channel}:{write.Port} {write.Remote} {action} opcode=0x{opcode:X4} {frame.Length} 瀛楄妭{reason} HEX={FormatNativeFrameHexForLog(frame, opcode)}");
        }
    }

    private static ushort ReadFirstNativeOpcode(byte[] frames)
    {
        if (frames.Length < 8)
            throw new InvalidDataException("原生响应头不足 8 字节。");
        return BinaryPrimitives.ReadUInt16LittleEndian(frames.AsSpan(6, 2));
    }

    private static byte[] BuildNativeFrame(
        byte[] request,
        ushort responseOpcode,
        ReadOnlySpan<byte> payload,
        ConnectionSession session)
    {
        if (request.Length < 8)
            throw new InvalidDataException("原生请求头不足 8 字节。");
        var totalLength = checked(8 + payload.Length);
        if (totalLength > ushort.MaxValue)
            throw new InvalidOperationException("原生响应过大。");

        var response = new byte[totalLength];
        // The writer loop owns sequence and checksum generation. Preserve the
        // request tag here so the first response can seed that ordered stream.
        response[0] = (byte)(request[0] & 0x1F);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(4, 2), (ushort)totalLength);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(6, 2), responseOpcode);
        payload.CopyTo(response.AsSpan(8));
        _ = session;
        return response;
    }

    private static byte[] BuildFriendRecommendationResultPayload(
        uint resultCode,
        string? recommendedCharacterName)
    {
        var payload = new byte[FriendRecommendationResponsePayloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), resultCode);
        if (resultCode == FriendRecommendationSuccess
            && !string.IsNullOrEmpty(recommendedCharacterName))
            WriteNullTerminatedGbk(
                payload.AsSpan(4, FriendRecommendationRequestPayloadLength),
                recommendedCharacterName);
        return payload;
    }

    private static void WriteNullTerminatedGbk(Span<byte> destination, string value)
    {
        destination.Clear();
        if (destination.Length <= 1 || string.IsNullOrEmpty(value))
            return;

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var strictGbk = Encoding.GetEncoding(
            936,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        strictGbk.GetEncoder().Convert(
            value.AsSpan(),
            destination[..^1],
            flush: true,
            out _,
            out _,
            out _);
    }

    private static void FinalizeNativeFramesForSend(byte[] frames, ConnectionSession session)
    {
        var offset = 0;
        while (offset < frames.Length)
        {
            if (frames.Length - offset < 8)
                throw new InvalidDataException("原生响应尾部不足 8 字节。");

            var length = BinaryPrimitives.ReadUInt16LittleEndian(frames.AsSpan(offset + 4, 2));
            if (length < 8 || length > frames.Length - offset)
                throw new InvalidDataException("原生响应包含无效帧长度。");

            var frame = frames.AsSpan(offset, length);
            if (!session.ResponseTransportTagInitialized)
            {
                session.ResponseTransportTagIndex = FindNativeTransportTagIndex((byte)(frame[0] & 0x1F));
                session.ResponseTransportTagInitialized = true;
            }

            var sequenceControl = BuildNativeTransportSequenceControl(
                session.ResponseTransportSequence,
                session.ResponseTransportTagIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(0, 2), sequenceControl);

            // C355 has several independent progress domains. Apply the
            // private all-open policy at the single final send boundary so a
            // future local, remote or reconnect constructor cannot emit only
            // the low-44 village mask while leaving the client's ordinary
            // dungeon/stage gates closed. This is intentionally before the
            // checksum and is idempotent with the primary C354 builder.
            NormalizeC355VillageAccessFrame(frame);

            var checksum = ComputeNativeChecksum(frame);
            var transportXorKey = session.ResponseTransportXorKey;
            var xorMask = (ushort)(transportXorKey | (transportXorKey << 8));
            BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(2, 2), (ushort)(checksum ^ xorMask));

            session.ResponseTransportSequence = session.ResponseTransportSequence >= NativeTransportMaximumSequence
                ? (byte)1
                : (byte)(session.ResponseTransportSequence + 1);
            session.ResponseTransportTagIndex = (byte)((session.ResponseTransportTagIndex + 1) % NativeTransportTags.Length);
            offset += length;
        }
    }

    private static ushort BuildNativeTransportSequenceControl(byte sequence, byte tagIndex)
    {
        if (sequence > NativeTransportMaximumSequence)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (tagIndex >= NativeTransportTags.Length)
            throw new ArgumentOutOfRangeException(nameof(tagIndex));
        return (ushort)(0xE000 | (sequence << 5) | NativeTransportTags[tagIndex]);
    }

    private static byte FindNativeTransportTagIndex(byte tag)
    {
        var index = Array.IndexOf(NativeTransportTags, tag);
        return index >= 0 ? (byte)index : (byte)0;
    }

    private static byte[] BuildLoadNecessityResponse(
        byte[] request,
        ConnectionSession session,
        byte[] dungeonClearMasks,
        byte[] dungeonBestRatings,
        byte[] dungeonSecretBestRatings,
        CoupleRelationRecord? coupleRelation)
    {
        var loadNecessity = BuildNativeFrame(
            request,
            0xC355,
            BuildLoadNecessityPayload(
                session.Character,
                dungeonClearMasks,
                dungeonBestRatings,
                dungeonSecretBestRatings,
                coupleRelation),
            session);
        // The C476 notification is the client's inventory initialization gate.
        // Push the equipped pet immediately after it as well: a reconnect in
        // the same client process can keep its inventory-loaded flags and skip
        // the otherwise automatic C44B request.
        var inventoryReadyPayload = new byte[4];
        // Every C476 status refreshes inventories. Status 2 has no message-box
        // branch, so initialization does not report a failed allocation.
        BinaryPrimitives.WriteUInt16LittleEndian(inventoryReadyPayload.AsSpan(0, 2), 2);
        var inventoryReady = BuildNativeFrame(
            request,
            0xC476,
            inventoryReadyPayload,
            session);
        var equippedPet = BuildNativeFrame(
            request,
            0xC44C,
            BuildPetInventoryPayload(session.Character),
            session);
        return CombineNativeFrames(loadNecessity, inventoryReady, equippedPet);
    }

    private static byte[] BuildLoadNecessityReadinessResponse(
        byte[] request,
        ConnectionSession session)
        => BuildNativeFrame(
            request,
            0xC594,
            BuildLoadNecessityReadinessPayload(),
            session);

    private static IEnumerable<byte[]> SplitNativeFrames(byte[] response)
    {
        var offset = 0;
        while (offset < response.Length)
        {
            if (response.Length - offset < 8)
                throw new InvalidDataException("原生响应尾部不足 8 字节。");

            var length = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(offset + 4, 2));
            if (length < 8 || length > response.Length - offset)
                throw new InvalidDataException("原生响应包含无效帧长度。");

            yield return response.AsSpan(offset, length).ToArray();
            offset += length;
        }
    }

    private static byte[] CombineNativeFrames(params byte[][] frames)
    {
        var totalLength = checked(frames.Sum(frame => frame.Length));
        var combined = new byte[totalLength];
        var offset = 0;
        foreach (var frame in frames)
        {
            frame.CopyTo(combined, offset);
            offset += frame.Length;
        }
        return combined;
    }

    private static bool TryReadTransportXorKey(ReadOnlySpan<byte> frame, out byte transportXorKey)
    {
        transportXorKey = 0x17;
        if (frame.Length < 8)
            return false;

        var protectedChecksum = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(2, 2));
        var xorMask = (ushort)(protectedChecksum ^ ComputeNativeChecksum(frame));
        var low = (byte)xorMask;
        var high = (byte)(xorMask >> 8);
        if (low != high)
            return false;

        transportXorKey = low;
        return true;
    }

    private static ushort ComputeNativeChecksum(ReadOnlySpan<byte> frame)
    {
        uint checksum = 0;
        for (var index = 4; index < frame.Length; index++)
            checksum += frame[index];
        return (ushort)checksum;
    }

    private static uint GetNativeLoginFailureCode(AccountAuthenticationStatus status)
        => status switch
        {
            AccountAuthenticationStatus.Banned => 3u,
            AccountAuthenticationStatus.IpBanned => 4u,
            AccountAuthenticationStatus.DoubleBanned => 5u,
            AccountAuthenticationStatus.AlreadyOnline => 6u,
            AccountAuthenticationStatus.DailyRegistrationLimitReached => 7u,
            AccountAuthenticationStatus.IpRegistrationLimitReached => 8u,
            AccountAuthenticationStatus.IpDailyRegistrationLimitReached => 9u,
            AccountAuthenticationStatus.AuthorizationRequired => 10u,
            AccountAuthenticationStatus.AccountTrialExpired => 11u,
            AccountAuthenticationStatus.GlobalTrialExpired => 12u,
            AccountAuthenticationStatus.AccountTrialNotAuthorized => 13u,
            _ => 0u
        };

    private static byte[] BuildLoginResponsePayload(uint resultCode, ConnectionSession session)
    {
        // The visible 0x2714 handler checks the answer dword at packet +8,
        // but its successful transition is VM-protected and consumes the
        // complete 24-byte packet object. Historical protocol validation for
        // this exact client also records a 24-byte total size. Keep the fixed
        // context deterministic so the protected code never reads beyond a
        // short receive allocation.
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), resultCode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4, 4),
            (uint)Math.Clamp(session.AccountId, 1L, (long)uint.MaxValue));
        payload[8] = (byte)(session.Character is null ? 0 : 1); // has_character
        payload[9] = 1; // one local adapter group
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(12, 4),
            (uint)Math.Clamp(session.Character?.Id ?? 0L, 0L, (long)uint.MaxValue));
        return payload;
    }

    private static byte[] BuildCharacterCreationResultPayload(bool accepted)
    {
        var payload = new byte[4];
        // The 0x2718 handler accepts 30 as success and 10 as a retryable name
        // error. The client must process 10 so it clears its create-in-progress
        // flag; an ignored value leaves the Create button permanently locked.
        BinaryPrimitives.WriteUInt16LittleEndian(payload, accepted ? (ushort)30 : (ushort)10);
        return payload;
    }

    private static byte[] BuildPostLoginPayload(ConnectionSession session)
    {
        // The 271A parser reads through packet+67 on success.
        var payload = new byte[60];
        // 30 is the successful login-context result. Values 10, 20 and 40
        // are error states (20 is rendered as "account already in game").
        // Character creation is selected by the success context below, not
        // by changing this result code.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 30);
        payload[2] = 1; // local adapter id
        // The 271A consumer reads frame+12 and calls the same character-level
        // setter used by C355. This is payload+4 after the native header.
        payload[4] = session.Character is null ? (byte)0 : (byte)Math.Clamp(session.Character.Level, 1, 99);
        // The standalone client routes packet+13 == 0 directly to
        // CMakeCharState and packet+13 == 1 to the existing-character path.
        payload[5] = (byte)(session.Character is null ? 0 : 1);
        // The 271A consumer stores packet+14 as the persistent guide state.
        // A completed character must restore state 2; state 0 makes the card
        // book recreate its basic-help flow every time it is opened.
        payload[6] = (byte)(session.Character is { TutorialCompleted: true } ? 2 : 0);
        if (session.Character is { } character)
        {
            // The successful 271A handler stores these fields as the local
            // player context later consumed by the C366 room/town path.
            WriteFixedGbk(payload.AsSpan(8, 16), character.Name);
            BuildStoredAppearance(character).CopyTo(payload, 24);
        }
        else
        {
            // This client requires a zero-level initial context and a nonempty login identity.
            WriteFixedGbk(payload.AsSpan(8, 16), session.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return payload;
    }

    private byte[] BuildChannelListPayload()
    {
        // 271C parser layout (relative to the 8-byte header):
        // +0 status, +2 uint16 count, then entries of
        // [uint16 population, byte channel index, byte reserved, uint32 IPv4].
        // The retail channel-entry constructor stores the uint16 at
        // packet+12+8*n as the current population and compares it with the
        // shared capacity at packet+172 to select its load indicator.
        // The parser also unconditionally reads packet+172 after iterating the
        // entries.  Therefore the native frame must be at least 176 bytes,
        // even when fewer than 20 channels are advertised.
        var payload = new byte[168];
        var endpoints = _endpoints
            .Where(item => item.Enabled)
            .OrderBy(item => item.Id)
            .Take(20)
            .ToArray();
        payload[0] = 0x64; // SUCCESS_TOPPIG used by the original client
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), checked((ushort)endpoints.Length));
        for (var index = 0; index < endpoints.Length; index++)
        {
            var endpoint = endpoints[index];
            var entryOffset = 4 + index * 8;
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(entryOffset, 2),
                checked((ushort)GetChannelPopulation(endpoint.Id)));
            payload[entryOffset + 2] = checked((byte)(endpoint.Id - 1));
            payload[entryOffset + 3] = 0;
            IPAddress.Parse(endpoint.Host).GetAddressBytes().CopyTo(payload, entryOffset + 4);
        }
        // packet+172 / payload+164 is the channel capacity shared by all
        // entries. The client treats population == capacity as full.
        var capacity = endpoints.Length == 0 ? 1000 : endpoints[0].Capacity;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(164, 4), (uint)capacity);
        return payload;
    }

    private static byte[] BuildArenaGameAdapterPayload(
        byte gameType,
        byte zone,
        string host,
        ushort port,
        ushort load)
    {
        if (gameType is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(gameType));
        if (!IPAddress.TryParse(host, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Arena game adapter host must be an IPv4 address.", nameof(host));

        var payload = new byte[ArenaAdapterResponsePayloadLength];
        payload[0] = 100;
        payload[1] = gameType;
        payload[2] = zone;
        payload[3] = 1;
        var addressBytes = Encoding.ASCII.GetBytes(address.ToString());
        if (addressBytes.Length >= ArenaAdapterAddressLength)
            throw new ArgumentException("Arena adapter IPv4 text does not fit its fixed field.", nameof(host));
        addressBytes.CopyTo(payload, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20, 2), port);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22, 2), load);
        return payload;
    }

    private async Task<byte[]> HandleLauncherAuthenticationAsync(
        byte[] payload,
        string? remoteIp,
        CancellationToken token)
    {
        PruneAuthenticationState();
        if (!_loginAuth.TryDecryptRequest(payload, out var request, out _))
        {
            _log($"登录器认证请求解密失败 source={remoteIp ?? "unknown"}");
            return BuildAuthenticationControlFrame(
                LoginAuthProtocol.AuthenticateResponseOpcode,
                LoginAuthProtocol.BuildResponse(0, 0, DateTime.UnixEpoch, "认证请求无效。"));
        }

        DateTime requestTimeUtc;
        try
        {
            requestTimeUtc = DateTimeOffset.FromUnixTimeSeconds(request.Timestamp).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            requestTimeUtc = DateTime.MinValue;
        }
        if ((DateTime.UtcNow - requestTimeUtc).Duration() > LoginAuthProtocol.RequestClockTolerance)
        {
            _log($"登录器认证请求已过期 account={request.Username} source={remoteIp ?? "unknown"}");
            return BuildAuthenticationControlFrame(
                LoginAuthProtocol.AuthenticateResponseOpcode,
                LoginAuthProtocol.BuildResponse(0, 0, DateTime.UnixEpoch, "认证请求已过期，请检查系统时间。"));
        }

        var nonceKey = Convert.ToHexString(request.Nonce);
        if (!_seenAuthNonces.TryAdd(nonceKey, DateTime.UtcNow.AddMinutes(3)))
        {
            _log($"拒绝重复的登录器认证请求 account={request.Username} source={remoteIp ?? "unknown"}");
            return BuildAuthenticationControlFrame(
                LoginAuthProtocol.AuthenticateResponseOpcode,
                LoginAuthProtocol.BuildResponse(0, 0, DateTime.UnixEpoch, "认证请求已使用，请重新登录。"));
        }

        var throttleKey = $"{remoteIp ?? "unknown"}|{request.Username}";
        if (IsAuthenticationBlocked(throttleKey, out var retryAfter))
        {
            _log($"登录失败次数过多 account={request.Username} source={remoteIp ?? "unknown"}");
            return BuildAuthenticationControlFrame(
                LoginAuthProtocol.AuthenticateResponseOpcode,
                LoginAuthProtocol.BuildResponse(0, 0, DateTime.UnixEpoch, $"尝试次数过多，请在 {retryAfter} 秒后重试。"));
        }

        AccountAuthenticationResult result;
        try
        {
            result = await _database.AuthenticateOrRegisterAsync(
                request.Username,
                request.Password,
                remoteIp,
                _loginPolicy,
                token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"登录认证数据库异常 account={request.Username} source={remoteIp ?? "unknown"} error={ex.GetType().Name}");
            return BuildAuthenticationControlFrame(
                LoginAuthProtocol.AuthenticateResponseOpcode,
                LoginAuthProtocol.BuildResponse(0, 0, DateTime.UnixEpoch, "登录服务暂时不可用。"));
        }

        if (!result.Success)
        {
            if (result.Status == AccountAuthenticationStatus.Failed)
                RecordAuthenticationFailure(throttleKey);
            _log($"登录认证失败 account={request.Username} source={remoteIp ?? "unknown"} result={result.Status}");
            if (result.AccountId > 0 && result.Status == AccountAuthenticationStatus.AuthorizationRequired)
                AccountStateChanged?.Invoke();
            return BuildAuthenticationControlFrame(
                LoginAuthProtocol.AuthenticateResponseOpcode,
                LoginAuthProtocol.BuildResponse((byte)result.Status, 0, DateTime.UnixEpoch, result.Error));
        }

        _authFailures.TryRemove(throttleKey, out _);
        var expiresAtUtc = DateTime.UtcNow.AddSeconds(45);
        uint ticket;
        do
        {
            ticket = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        }
        while (!_pendingLaunchTickets.TryAdd(
            ticket,
            new LaunchTicket(result.AccountId, result.Username, remoteIp, expiresAtUtc)));

        _log($"登录认证成功 account={result.Username} accountId={result.AccountId} source={remoteIp ?? "unknown"} result={result.Status}");
        if (result.Status == AccountAuthenticationStatus.Registered)
            AccountStateChanged?.Invoke();
        var status = result.Status == AccountAuthenticationStatus.Registered ? (byte)2 : (byte)1;
        var successMessage = status == 2 ? "账号已创建，正在进入游戏。" : "登录成功，正在进入游戏。";
        return BuildAuthenticationControlFrame(
            LoginAuthProtocol.AuthenticateResponseOpcode,
            LoginAuthProtocol.BuildResponse(status, ticket, expiresAtUtc, successMessage));
    }

    private static byte[] BuildAuthenticationControlFrame(ushort opcode, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[8 + payload.Length];
        frame[0] = 0x0E;
        frame[1] = 0xE0;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), (ushort)frame.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), opcode);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    private bool IsAuthenticationBlocked(string key, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!_authFailures.TryGetValue(key, out var state))
            return false;
        lock (state.Gate)
        {
            var now = DateTime.UtcNow;
            if (state.BlockedUntilUtc <= now)
                return false;
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((state.BlockedUntilUtc - now).TotalSeconds));
            return true;
        }
    }

    private void RecordAuthenticationFailure(string key)
    {
        var state = _authFailures.GetOrAdd(key, _ => new AuthFailureState());
        lock (state.Gate)
        {
            var now = DateTime.UtcNow;
            if (now - state.WindowStartedUtc > TimeSpan.FromMinutes(5))
            {
                state.WindowStartedUtc = now;
                state.Count = 0;
            }
            state.Count++;
            if (state.Count >= 5)
                state.BlockedUntilUtc = now.AddMinutes(5);
        }
    }

    private void PruneAuthenticationState()
    {
        var now = DateTime.UtcNow;
        foreach (var item in _pendingLaunchTickets)
            if (item.Value.ExpiresAtUtc <= now)
                _pendingLaunchTickets.TryRemove(item.Key, out _);
        foreach (var item in _seenAuthNonces)
            if (item.Value <= now)
                _seenAuthNonces.TryRemove(item.Key, out _);
        foreach (var item in _authFailures)
        {
            lock (item.Value.Gate)
            {
                if (item.Value.BlockedUntilUtc <= now
                    && now - item.Value.WindowStartedUtc > TimeSpan.FromMinutes(10))
                    _authFailures.TryRemove(item.Key, out _);
            }
        }
    }

    private void CacheLoginTicket(ConnectionSession session, bool channelReentry = false)
    {
        if (session.AccountId <= 0 || string.IsNullOrWhiteSpace(session.Username))
            return;
        var ticket = new LoginTicket(
            session.AccountId,
            session.Username,
            session.Character?.Name,
            session.RemoteIp,
            DateTime.UtcNow.Add(channelReentry ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10)),
            channelReentry);
        _loginTicketsByUsername[session.Username] = ticket;
        if (!string.IsNullOrWhiteSpace(ticket.CharacterName))
            _loginTicketsByCharacterName[ticket.CharacterName] = ticket;
        _loginTicketsByAccountId[session.AccountId] = ticket;
        PruneLoginTickets();
    }

    private int CacheUnambiguousChannelReentryTicket(string? remoteIp)
    {
        if (string.IsNullOrWhiteSpace(remoteIp))
            return 0;
        var matches = _activeWorldSessions.Values
            .Where(item => item.Session.OnlineTracked
                           && !item.Session.AuxiliaryGameSession
                           && string.Equals(item.RemoteIp, remoteIp, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matches.Length == 1)
            CacheLoginTicket(matches[0].Session, channelReentry: true);
        return matches.Length;
    }

    private async Task<bool> WaitForChannelReentryReleaseAsync(
        LoginTicket ticket,
        string? remoteIp,
        CancellationToken token)
    {
        if (!ticket.ChannelReentry)
            return true;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        do
        {
            var active = _activeWorldSessions.Values
                .FirstOrDefault(item => item.AccountId == ticket.AccountId);
            if (active is null)
                return true;
            if (!string.Equals(active.RemoteIp, remoteIp, StringComparison.OrdinalIgnoreCase))
                return false;
            await Task.Delay(10, token);
        }
        while (DateTime.UtcNow < deadline);
        return !_activeWorldSessions.Values.Any(item => item.AccountId == ticket.AccountId);
    }

    private void PruneLoginTickets()
    {
        var now = DateTime.UtcNow;
        foreach (var item in _loginTicketsByUsername)
            if (item.Value.ExpiresAtUtc <= now)
                _loginTicketsByUsername.TryRemove(item.Key, out _);
        foreach (var item in _loginTicketsByCharacterName)
            if (item.Value.ExpiresAtUtc <= now)
                _loginTicketsByCharacterName.TryRemove(item.Key, out _);
        foreach (var item in _loginTicketsByAccountId)
            if (item.Value.ExpiresAtUtc <= now)
                _loginTicketsByAccountId.TryRemove(item.Key, out _);
    }

    private async Task<bool> RestoreWorldSessionAsync(
        string identityText,
        string? remoteIp,
        ConnectionSession session,
        CancellationToken token)
    {
        PruneLoginTickets();
        LoginTicket? ticket = null;
        if (!string.IsNullOrWhiteSpace(identityText))
        {
            // The native client hands the character name from 0x271A back in
            // C351. Username and numeric account id remain compatibility
            // fallbacks for older locally patched clients.
            _loginTicketsByCharacterName.TryGetValue(identityText, out ticket);
            if (ticket is null)
                _loginTicketsByUsername.TryGetValue(identityText, out ticket);
            if (ticket is null
                && long.TryParse(identityText, out var numericIdentity)
                && numericIdentity > 0)
                _loginTicketsByAccountId.TryGetValue(numericIdentity, out ticket);
        }

        if (ticket is null)
            return false;
        if (ticket.RemoteIp is not null
            && !string.Equals(ticket.RemoteIp, remoteIp, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!await WaitForChannelReentryReleaseAsync(ticket, remoteIp, token))
            return false;
        var account = await _database.GetAccountAccessByIdAsync(ticket.AccountId, token);
        if (account is null || account.Value.IsBanned)
            return false;
        session.AccountId = account.Value.Id;
        session.Username = account.Value.Username;
        session.Character = await _database.GetCharacterAsync(session.AccountId, token);
        if (session.Character is null)
            return false;
        if (!string.IsNullOrWhiteSpace(ticket.CharacterName)
            && !string.Equals(ticket.CharacterName, session.Character.Name, StringComparison.OrdinalIgnoreCase))
            return false;
        session.RemoteIp = remoteIp;
        var restoredPosition = TownPositionPolicy.Normalize(
            session.Character.PositionX,
            session.Character.PositionY);
        if (restoredPosition.Repaired)
        {
            _log($"World session repaired persisted town position before C355/C367: character={session.Character.Name} map={session.Character.CurrentMapId} page={session.Character.CurrentTownPage} old=({session.Character.PositionX},{session.Character.PositionY}) new=({restoredPosition.X},{restoredPosition.Y})");
            session.Character.PositionX = restoredPosition.X;
            session.Character.PositionY = restoredPosition.Y;
        }
        // The unmodified client turns the first C355 of every process into a
        // one-shot FirstVillageFlag actor creation. It ignores C368 coordinates
        // in that branch, so bootstrap on the proven 0/0 page instead of loading
        // an arbitrary persisted page whose first-entry point may be (-1,-1).
        session.TownId = TownPositionPolicy.LoginBootstrapMapId;
        session.TownPage = TownPositionPolicy.LoginBootstrapTownPage;
        session.LastReportedPositionX = TownPositionPolicy.FallbackX;
        session.LastReportedPositionY = TownPositionPolicy.FallbackY;
        if (session.ChannelId <= 0 || !_endpoints.Any(item => item.Enabled && item.Id == session.ChannelId))
            session.ChannelId = _endpoints.FirstOrDefault(item => item.Enabled)?.Id ?? 1;
        var connected = await TrackConnectedAsync(session, remoteIp, token);
        if (connected && session.Character.CurrentHp <= 0)
        {
            session.Character.CurrentHp = Math.Max(1, session.Character.MaxHp);
            session.Character.CurrentMp = Math.Max(1, session.Character.MaxMp);
            await _database.SaveCharacterRuntimeStateAsync(
                session.AccountId,
                session.Character.Id,
                session.SessionId,
                CreateRuntimeState(session.Character, session.ChannelId),
                token);
        }
        if (connected)
            ConsumeLoginTicket(ticket);
        return connected;
    }

    private void ConsumeLoginTicket(LoginTicket ticket)
    {
        if (_loginTicketsByUsername.TryGetValue(ticket.Username, out var usernameTicket)
            && usernameTicket == ticket)
            _loginTicketsByUsername.TryRemove(ticket.Username, out _);
        if (!string.IsNullOrWhiteSpace(ticket.CharacterName)
            && _loginTicketsByCharacterName.TryGetValue(ticket.CharacterName, out var characterTicket)
            && characterTicket == ticket)
            _loginTicketsByCharacterName.TryRemove(ticket.CharacterName, out _);
        if (_loginTicketsByAccountId.TryGetValue(ticket.AccountId, out var accountIdTicket)
            && accountIdTicket == ticket)
            _loginTicketsByAccountId.TryRemove(ticket.AccountId, out _);
    }

    private async Task<bool> TrackConnectedAsync(
        ConnectionSession session,
        string? remoteIp,
        CancellationToken token)
    {
        if (session.OnlineTracked || session.AccountId <= 0 || session.Character is null)
            return session.OnlineTracked;
        await _presenceGate.WaitAsync(token);
        try
        {
            if (session.OnlineTracked)
                return true;
            if (_activeWorldSessions.Values.Any(item => item.AccountId == session.AccountId))
            {
                _log($"账号 {session.AccountId} 已存在活动世界会话，拒绝重复连接");
                return false;
            }
            var persisted = await _database.BeginWorldSessionAsync(
                session.AccountId,
                session.Character.Id,
                session.SessionId,
                session.ChannelId,
                remoteIp,
                token);
            if (!persisted)
                return false;
            session.OnlineTracked = true;
            session.Character.IsOnline = true;
            session.Character.CurrentChannelId = session.ChannelId;
            var onlineSinceUtc = DateTime.UtcNow;
            _activeWorldSessions[session.SessionId] = new WorldPresence(
                session,
                session.SessionId,
                session.AccountId,
                session.Character.Id,
                session.Username,
                session.Character.Name,
                remoteIp,
                session.ChannelId,
                onlineSinceUtc,
                session.LastActivityUtc,
                reason =>
                {
                    session.DisconnectReason = reason;
                    try
                    {
                        session.ConnectionCancellation?.Cancel();
                    }
                    catch (ObjectDisposedException) { }
                });

            var trialAccess = await _database.GetAccountTrialAccessAsync(
                session.AccountId,
                _loginPolicy,
                token);
            ScheduleTrialExpiration(session, trialAccess);
        }
        finally
        {
            _presenceGate.Release();
        }
        AccountStateChanged?.Invoke();
        return true;
    }

    private void ScheduleTrialExpiration(ConnectionSession session, AccountTrialAccess access)
    {
        if (!access.Limited)
            return;
        var adapterToken = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                if (access.RemainingSeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(access.RemainingSeconds), adapterToken);
                if (adapterToken.IsCancellationRequested
                    || !_activeWorldSessions.TryGetValue(session.SessionId, out var presence)
                    || presence.AccountId != session.AccountId)
                    return;
                var reason = access.ExpiredStatus == AccountAuthenticationStatus.AccountTrialExpired
                    ? "单账号试玩时间已到"
                    : "全服账号试玩时间已到";
                _log($"账号 {session.AccountId} {reason}，立即断开主世界会话");
                presence.RequestDisconnect(reason);
            }
            catch (OperationCanceledException) when (adapterToken.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);
    }

    private async Task TrackDisconnectedAsync(ConnectionSession session)
    {
        if (!session.OnlineTracked || session.Character is null)
            return;
        if (session.AuxiliaryGameSession)
        {
            _activeArenaSessions.TryRemove(session.SessionId, out _);
            if (session.ArenaGameType is >= 1 and <= 3)
                RemoveEntertainmentRoomMember(session);
            else
                RemoveArenaRoomMember(session);
            session.OnlineTracked = false;
            _log($"天空竞技场辅助连接已释放：type={session.ArenaGameType} character={session.Character.Name}；主世界会话保持在线");
            return;
        }
        await _presenceGate.WaitAsync();
        try
        {
            if (!session.OnlineTracked)
                return;
            if (session.Character.CurrentHp <= 0
                && GetDungeonRoom(session) is not null
                && !TryGetDungeonTransition(session, out _, out _))
            {
                session.Character.CurrentHp = Math.Max(1, session.Character.MaxHp);
                session.Character.CurrentMp = Math.Max(1, session.Character.MaxMp);
                _log($"Dungeon disconnect restored dead character before persistence: characterId={session.Character.Id} hp={session.Character.CurrentHp}/{session.Character.MaxHp} mp={session.Character.CurrentMp}/{session.Character.MaxMp}");
            }
            session.OnlineTracked = false;
            RemoveDungeonRoomMember(session);
            _activeWorldSessions.TryRemove(session.SessionId, out _);
            var persisted = await _database.EndWorldSessionAsync(
                session.AccountId,
                session.Character.Id,
                session.SessionId,
                CreateRuntimeState(session.Character, session.ChannelId));
            if (persisted)
            {
                session.Character.IsOnline = false;
                session.Character.CurrentChannelId = null;
            }
            else
            {
                _log($"璐﹀彿 {session.AccountId} 鏂嚎鐘舵€佹湭淇濆瓨锛氭暟鎹簱娲诲姩浼氳瘽涓庡綋鍓嶈繛鎺ヤ笉鍖归厤");
            }
        }
        catch (Exception ex)
        {
            _log($"Cleanup presence failed: {ex.Message}");
        }
        finally
        {
            _presenceGate.Release();
        }
        await CleanupMentorSessionAsync(session);
        AccountStateChanged?.Invoke();
    }

    private async Task CleanupMentorSessionAsync(ConnectionSession session)
    {
        if (session.Character is null)
            return;

        var stateChanged = _mentorAdvertisingCharacters.TryRemove(session.Character.Id, out _);
        try
        {
            await _database.SetMentorAdvertisingAsync(session.Character.Id, false);
        }
        catch (Exception ex)
        {
            _log($"角色断线时停止家教广告失败：character={session.Character.Name} error={ex.Message}");
        }

        MentorPendingRequest[] abandoned;
        lock (_mentorGate)
        {
            var keys = _mentorPendingRequests
                .Where(pair => pair.Value.Requester.SessionId == session.SessionId
                               || pair.Value.Responder.SessionId == session.SessionId)
                .Select(pair => pair.Key)
                .ToArray();
            abandoned = keys.Select(key => _mentorPendingRequests[key]).ToArray();
            foreach (var key in keys)
                _mentorPendingRequests.Remove(key);
        }
        lock (_coupleGate)
        {
            foreach (var key in _couplePendingRequests
                         .Where(pair => pair.Value.Requester.SessionId == session.SessionId
                                        || pair.Value.Responder.SessionId == session.SessionId)
                         .Select(pair => pair.Key)
                         .ToArray())
                _couplePendingRequests.Remove(key);
        }

        foreach (var pending in abandoned)
        {
            try
            {
                await _database.CompleteMentorInteractionAsync(
                    pending.InteractionId,
                    MentorProtocol.TimedOut);
            }
            catch (Exception ex)
            {
                _log($"角色断线时收口师生请求失败：interaction={pending.InteractionId} error={ex.Message}");
            }

            if (pending.Responder.SessionId != session.SessionId
                || !_activeWorldSessions.TryGetValue(pending.Requester.SessionId, out var requester)
                || requester.Session.Character is null)
                continue;
            try
            {
                await SendNativeBroadcastAsync(
                    new PendingNativeBroadcast(
                        requester,
                        MentorProtocol.GetResponseOpcode(pending.RequestOpcode),
                        MentorProtocol.BuildUnavailableResponse(
                            pending.RequestOpcode,
                            pending.RequestPayload,
                            pending.Responder.CharacterName),
                        $"mentor responder disconnected interaction {pending.InteractionId}"),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log($"师生目标断线通知失败：interaction={pending.InteractionId} error={ex.Message}");
            }
        }

        if (stateChanged || abandoned.Length > 0)
            MentorStateChanged?.Invoke();
    }

    private async Task<HealthRecoveryResolution> ApplyNonCombatHealthRecoveryAsync(
        ConnectionSession session,
        HealthRecoveryScene scene,
        CancellationToken token)
    {
        if (session.Character is null)
            return default;

        var battleEpochActive = session.NativeDungeon is not null
            || session.NativeForwarding
            || session.NativeCheckpoint is not null
            || session.DungeonRoomId > 0;
        var resolution = HealthRecoveryPolicy.Resolve(
            session.Character,
            scene,
            session.OnlineTracked,
            battleEpochActive);
        if (!resolution.Changed)
            return resolution;

        var persisted = await _database.ApplyHealthRecoveryStepAsync(
            session.AccountId,
            session.Character.Id,
            session.SessionId,
            scene,
            token);
        if (!persisted.Applied)
        {
            await RefreshSessionCharacterAsync(session, token);
            return default;
        }

        session.Character.CurrentHp = persisted.CurrentHp;
        session.Character.CurrentMp = persisted.CurrentMp;
        await RefreshSessionCharacterAsync(session, token);
        AccountStateChanged?.Invoke();
        return resolution with
        {
            CurrentHp = persisted.CurrentHp,
            CurrentMp = persisted.CurrentMp
        };
    }

    private async Task RefreshSessionCharacterAsync(ConnectionSession session, CancellationToken token)
    {
        if (session.AccountId <= 0)
            return;
        var refreshed = await _database.GetCharacterAsync(session.AccountId, token);
        if (refreshed is not null)
        {
            if (session.OnlineTracked && session.Character is { } runtimeCharacter)
            {
                refreshed.CurrentMapId = runtimeCharacter.CurrentMapId;
                refreshed.CurrentTownPage = runtimeCharacter.CurrentTownPage;
                refreshed.PositionX = runtimeCharacter.PositionX;
                refreshed.PositionY = runtimeCharacter.PositionY;
            }
            session.Character = refreshed;
        }
    }

    private static CharacterRuntimeState CreateRuntimeState(CharacterRecord character, int channelId)
        => new(
            character.CurrentHp,
            character.CurrentMp,
            character.CurrentMapId,
            character.CurrentTownPage,
            character.PositionX,
            character.PositionY,
            channelId);

    private bool TryRestoreAuxiliaryGameSession(
        string identityText,
        string? remoteIp,
        ConnectionSession session)
    {
        var matches = _activeWorldSessions.Values
            .Where(item => string.Equals(item.CharacterName, identityText, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.RemoteIp is null
                || string.Equals(item.RemoteIp, remoteIp, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1 || matches[0].Session.Character is null)
            return false;

        var source = matches[0];
        session.AccountId = source.AccountId;
        session.Username = source.Username;
        session.Character = source.Session.Character;
        session.ChannelId = source.ChannelId;
        session.RemoteIp = remoteIp;
        session.OnlineTracked = true;
        session.AuxiliaryGameSession = true;
        return true;
    }

    private ArenaRoom CreateArenaRoom(
        ConnectionSession owner,
        ArenaCreateRequest createRequest)
    {
        lock (_arenaRoomGate)
        {
            RemoveArenaRoomMemberLocked(owner);
            return CreateArenaRoomLocked(owner, createRequest);
        }
    }

    private ArenaRoom CreateArenaRoomLocked(
        ConnectionSession owner,
        ArenaCreateRequest createRequest)
    {
        var room = new ArenaRoom
        {
            Id = NextArenaRoomIdLocked(),
            ChannelId = owner.ChannelId,
            GameType = owner.ArenaGameType,
            OwnerSessionId = owner.SessionId,
            CreateRequest = createRequest
        };
        room.Members.Add(owner.SessionId, owner);
        _arenaRooms.Add(room.Id, room);
        owner.ArenaRoomId = room.Id;
        owner.ArenaSlotIndex = 0;
        owner.ArenaReady = false;
        owner.ArenaTeamCode = GetDefaultArenaTeamCode(owner.ArenaSlotIndex);
        owner.ArenaMulticastInitialized = false;
        ResetP2PState(owner);
        return room;
    }

    private ArenaRoom? QuickEnterArenaRoom(
        ConnectionSession member,
        in ArenaQuickEnterRequest request,
        out bool created)
    {
        lock (_arenaRoomGate)
        {
            created = false;
            if (member.ArenaRoomId != 0)
                RemoveArenaRoomMemberLocked(member);

            ArenaRoom? room = null;
            if (request.Mode == 20)
            {
                if (request.RoomId is not (0 or ushort.MaxValue))
                    _arenaRooms.TryGetValue(request.RoomId, out room);
            }
            else
            {
                room = _arenaRooms.Values
                    .Where(candidate => candidate.ChannelId == member.ChannelId
                        && candidate.GameType == member.ArenaGameType
                        && CanJoinArenaRoomLocked(candidate, out _))
                    .OrderBy(candidate => candidate.Id)
                    .FirstOrDefault();
            }

            if (room is null && request.Mode == 100 && member.Character is not null)
            {
                var automaticRequest = ArenaProtocol.CreateAutomaticRoomRequest(
                    member.Character.Name,
                    request);
                room = CreateArenaRoomLocked(member, automaticRequest);
                created = true;
                return room;
            }

            if (room is null
                || room.ChannelId != member.ChannelId
                || room.GameType != member.ArenaGameType
                || !CanJoinArenaRoomLocked(room, out var freeSlot))
                return null;

            room.Members[member.SessionId] = member;
            room.GameDataPayload = [];
            member.ArenaRoomId = room.Id;
            member.ArenaSlotIndex = freeSlot;
            member.ArenaReady = false;
            member.ArenaTeamCode = GetDefaultArenaTeamCode(member.ArenaSlotIndex);
            member.ArenaMulticastInitialized = false;
            ResetP2PState(member);
            return room;
        }
    }

    private bool TryJoinArenaRoom(
        ConnectionSession member,
        ushort roomId,
        string password,
        out ArenaRoom? joinedRoom)
    {
        lock (_arenaRoomGate)
        {
            joinedRoom = null;
            if (!_arenaRooms.TryGetValue(roomId, out var room)
                || room.ChannelId != member.ChannelId
                || room.GameType != member.ArenaGameType
                || !string.Equals(room.Password, password, StringComparison.Ordinal))
                return false;

            if (room.Members.ContainsKey(member.SessionId))
            {
                joinedRoom = room;
                return true;
            }
            if (!CanJoinArenaRoomLocked(room, out var freeSlot))
                return false;

            RemoveArenaRoomMemberLocked(member);
            room.Members.Add(member.SessionId, member);
            room.GameDataPayload = [];
            member.ArenaRoomId = room.Id;
            member.ArenaSlotIndex = freeSlot;
            member.ArenaReady = false;
            member.ArenaTeamCode = GetDefaultArenaTeamCode(member.ArenaSlotIndex);
            member.ArenaMulticastInitialized = false;
            ResetP2PState(member);
            joinedRoom = room;
            return true;
        }
    }

    private static bool CanJoinArenaRoomLocked(ArenaRoom room, out byte freeSlot)
    {
        freeSlot = 0;
        if (room.Started || room.Members.Count >= 4)
            return false;

        var occupiedSlots = room.Members.Values
            .Select(roomMember => roomMember.ArenaSlotIndex)
            .ToHashSet();
        for (byte slot = 1; slot <= 3; slot++)
        {
            if (!occupiedSlots.Contains(slot) && room.SlotStates[slot - 1] != 2)
            {
                freeSlot = slot;
                return true;
            }
        }
        return false;
    }

    private int NextArenaRoomIdLocked()
    {
        do
        {
            _nextArenaRoomId = _nextArenaRoomId >= ushort.MaxValue - 1
                ? 1
                : _nextArenaRoomId + 1;
        }
        while (_arenaRooms.ContainsKey(_nextArenaRoomId));
        return _nextArenaRoomId;
    }

    private void RemoveArenaRoomMember(
        ConnectionSession member,
        ConnectionSession? broadcastSource = null)
    {
        lock (_arenaRoomGate)
            RemoveArenaRoomMemberLocked(member, broadcastSource ?? member);
    }

    private void RemoveArenaRoomMemberLocked(
        ConnectionSession member,
        ConnectionSession? broadcastSource = null)
    {
        if (member.ArenaRoomId == 0
            || !_arenaRooms.TryGetValue(member.ArenaRoomId, out var room))
        {
            ResetArenaRoomState(member);
            return;
        }

        room.Members.Remove(member.SessionId);
        room.GameDataPayload = [];
        room.EndValuesBySession.Remove(member.SessionId);
        room.EndingSessionIds.Remove(member.SessionId);
        room.ResultSessionIds.Remove(member.SessionId);
        room.ResetSessionIds.Remove(member.SessionId);
        room.CurrentHpBySession.Remove(member.SessionId);
        room.ScoreBySession.Remove(member.SessionId);
        room.EntityInitializedSessions.Remove(member.SessionId);
        room.EntityAnnouncements.RemoveWhere(key =>
            key.StartsWith(member.SessionId + "\0", StringComparison.Ordinal)
            || key.EndsWith("\0" + member.SessionId, StringComparison.Ordinal));
        if (room.Members.Count == 0)
        {
            _arenaRooms.Remove(room.Id);
        }
        else if (room.OwnerSessionId == member.SessionId)
        {
            var newOwner = room.Members.Values
                .OrderBy(candidate => candidate.ArenaSlotIndex)
                .First();
            room.OwnerSessionId = newOwner.SessionId;
            newOwner.ArenaReady = false;
            newOwner.ArenaTeamCode = GetDefaultArenaTeamCode(newOwner.ArenaSlotIndex);
            QueueArenaAuthorityRefreshLocked(room, broadcastSource ?? member, newOwner);
        }
        if (room.Members.Count != 0)
            TryCompleteArenaRoundResetLocked(room);
        ResetArenaRoomState(member);
    }

    private static void QueueArenaAuthorityRefreshLocked(
        ArenaRoom room,
        ConnectionSession queueSource,
        ConnectionSession owner)
    {
        if (owner.Character is null)
            return;
        foreach (var recipient in room.Members.Values)
        foreach (var entity in room.Members.Values)
        {
            if (entity.Character is null)
                continue;
            queueSource.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
                recipient,
                0xCF71,
                BuildGameRoomUserPayload(entity.Character, owner.Character, entity.ArenaSlotIndex),
                "arena owner authority refresh"));
            queueSource.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
                recipient,
                0xCF7E,
                BuildGameReadyPayload(
                    entity.Character,
                    entity.ArenaReady,
                    entity.ArenaTeamCode),
                "arena owner authority ready/team refresh"));
            if (entity.ArenaMulticastInitialized)
            {
                queueSource.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
                    recipient,
                    0xCFDA,
                    BuildMulticastingGameEventPayload(entity.Character),
                    "arena owner authority entity activation"));
            }
        }
    }

    private static void ResetArenaRoomState(ConnectionSession member)
    {
        member.ArenaRoomId = 0;
        member.ArenaSlotIndex = 0;
        member.ArenaReady = false;
        member.ArenaTeamCode = 0;
        member.ArenaMulticastInitialized = false;
        ResetP2PState(member);
    }

    private ArenaRoom? GetArenaRoom(ConnectionSession member)
    {
        lock (_arenaRoomGate)
            return member.ArenaRoomId != 0
                   && _arenaRooms.TryGetValue(member.ArenaRoomId, out var room)
                   && room.Members.ContainsKey(member.SessionId)
                ? room
                : null;
    }

    private void QueueArenaMemberSnapshots(ConnectionSession initialized)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(initialized.ArenaRoomId, out var room)
                || initialized.Character is null
                || !room.Members.ContainsKey(initialized.SessionId)
                || !room.Members.TryGetValue(room.OwnerSessionId, out var owner)
                || owner.Character is null)
                return;

            room.EntityInitializedSessions.Add(initialized.SessionId);
            initialized.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
                initialized,
                0xCF7E,
                BuildGameReadyPayload(
                    initialized.Character,
                    initialized.ArenaReady,
                    initialized.ArenaTeamCode),
                "local arena ready/team snapshot"));
            foreach (var peer in room.Members.Values)
            {
                if (peer.SessionId == initialized.SessionId
                    || peer.Character is null
                    || !room.EntityInitializedSessions.Contains(peer.SessionId))
                    continue;

                QueueArenaEntityAnnouncementLocked(
                    room, initialized, initialized, peer, owner.Character, "existing arena member snapshot");
                QueueArenaEntityAnnouncementLocked(
                    room, initialized, peer, initialized, owner.Character, "arena member entered");
            }
        }
    }

    private static void QueueArenaEntityAnnouncementLocked(
        ArenaRoom room,
        ConnectionSession source,
        ConnectionSession recipient,
        ConnectionSession entity,
        CharacterRecord ownerCharacter,
        string reason)
    {
        var announcementKey = $"{recipient.SessionId}\0{entity.SessionId}";
        if (!room.EntityAnnouncements.Add(announcementKey) || entity.Character is null)
            return;

        source.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
            recipient,
            0xCF71,
            BuildGameRoomUserPayload(entity.Character, ownerCharacter, entity.ArenaSlotIndex),
            reason));
        if (entity.ArenaMulticastInitialized)
        {
            source.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
                recipient,
                0xCFDA,
                BuildMulticastingGameEventPayload(entity.Character),
                "existing arena member entity activation"));
        }
        if (entity.ArenaReady || entity.ArenaTeamCode != 0)
        {
            source.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
                recipient,
                0xCF7E,
                BuildGameReadyPayload(entity.Character, entity.ArenaReady, entity.ArenaTeamCode),
                "existing arena ready/team snapshot"));
        }
    }

    private ConnectionSession? FindArenaRoomOwner(ConnectionSession requester)
    {
        lock (_arenaRoomGate)
        {
            return _arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                   && room.Members.TryGetValue(room.OwnerSessionId, out var owner)
                ? owner
                : null;
        }
    }

    private ConnectionSession? FindArenaRoomMember(ConnectionSession requester, uint cursor)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || !room.Members.TryGetValue(requester.SessionId, out var member))
                return null;
            // The retail request writes an opaque UI cursor, not a character id.
            _ = cursor;
            return member;
        }
    }

    private ConnectionSession? FindArenaRoomMemberByUid(
        ConnectionSession requester,
        uint uid)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId))
                return null;
            return room.Members.Values.FirstOrDefault(member =>
                member.Character is { } character
                && GetSceneEntityId(character) == uid);
        }
    }

    private CharacterRecord? FindArenaRoomOwnerAfterLeave(ConnectionSession leaving)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(leaving.ArenaRoomId, out var room))
                return null;
            if (room.OwnerSessionId != leaving.SessionId)
                return room.Members.GetValueOrDefault(room.OwnerSessionId)?.Character;
            return room.Members.Values
                .Where(member => member.SessionId != leaving.SessionId)
                .OrderBy(member => member.ArenaSlotIndex)
                .Select(member => member.Character)
                .FirstOrDefault(character => character is not null);
        }
    }

    private bool TrySetArenaReady(ConnectionSession requester, bool ready, byte teamCode)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId)
                || room.Started
                || teamCode > 2)
                return false;
            requester.ArenaReady = ready;
            requester.ArenaTeamCode = teamCode;
            return true;
        }
    }

    private static byte GetDefaultArenaTeamCode(byte slotIndex)
        => (byte)((slotIndex & 1) == 0 ? 1 : 2);

    private bool TrySetArenaSlotState(
        ConnectionSession requester,
        byte slotIndex,
        byte state)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || room.OwnerSessionId != requester.SessionId
                || room.Started
                || slotIndex >= room.SlotStates.Length
                || state is not (0 or 2)
                || (state == 2 && room.Members.Values.Any(member =>
                    member.ArenaSlotIndex == slotIndex + 1)))
                return false;
            room.SlotStates[slotIndex] = state;
            return true;
        }
    }

    private bool TrySetArenaModeAndMap(
        ConnectionSession requester,
        ushort selectedMode,
        ushort selectedMap)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || room.OwnerSessionId != requester.SessionId
                || room.Started
                || selectedMode != 0
                || selectedMap > ArenaProtocol.ArenaMapCount)
                return false;
            (room.SelectedMode, room.SelectedMap) = ArenaProtocol.ResolveGameSelectors(
                selectedMode,
                selectedMap);
            room.GameDataPayload = [];
            return true;
        }
    }

    private bool TryMarkArenaRoundEnding(ConnectionSession requester, out int roomId)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId))
            {
                roomId = 0;
                return false;
            }

            roomId = room.Id;
            if (!room.Started)
                return room.EndingSessionIds.Contains(requester.SessionId);
            room.EndingSessionIds.Add(requester.SessionId);
            return true;
        }
    }

    private bool TryBuildArenaGameEventResult(
        ConnectionSession requester,
        in ArenaGameEventRequest request,
        out int roomId,
        out byte[] responsePayload,
        out ushort hpBefore,
        out ushort hpAfter,
        out int scoreBefore,
        out int scoreAfter)
    {
        roomId = 0;
        responsePayload = [];
        hpBefore = 0;
        hpAfter = 0;
        scoreBefore = 0;
        scoreAfter = 0;

        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || !room.Started
                || !room.Members.ContainsKey(requester.SessionId)
                || requester.Character is null)
                return false;

            roomId = room.Id;
            var maximumHp = (ushort)Math.Clamp(requester.Character.MaxHp, 1, ushort.MaxValue);
            if (!room.CurrentHpBySession.TryGetValue(requester.SessionId, out hpBefore))
                hpBefore = maximumHp;
            hpAfter = (ushort)Math.Max(0, hpBefore - request.PlayerDamage);
            room.CurrentHpBySession[requester.SessionId] = hpAfter;

            scoreBefore = room.ScoreBySession.GetValueOrDefault(requester.SessionId);
            scoreAfter = scoreBefore;
            if (request.EventCode is 20 or 30)
            {
                var scoreDelta = ArenaProtocol.ResolveScoreDelta(
                    room.GameDataPayload,
                    request.PrimaryUid);
                scoreAfter = (int)Math.Clamp(
                    (long)scoreBefore + scoreDelta,
                    0L,
                    int.MaxValue);
                room.ScoreBySession[requester.SessionId] = scoreAfter;
            }

            responsePayload = ArenaProtocol.BuildGameEventResult(
                GetSceneEntityId(requester.Character),
                hpAfter,
                scoreAfter,
                request);
            return true;
        }
    }

    private async Task<(int RoomId, byte[] Payload, int RecordCount, bool AllActiveResultsReceived)?>
        CollectArenaRoundResultAsync(
            ConnectionSession requester,
            uint reportedValue,
            CancellationToken token)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId)
                || (!room.Started && !room.ResultSessionIds.Contains(requester.SessionId))
                || (room.Started && !room.EndingSessionIds.Contains(requester.SessionId)))
                return null;

            if (room.ResultSessionIds.Add(requester.SessionId))
                room.EndValuesBySession[requester.SessionId] = reportedValue;
            if (room.PvpResultPayload.Length != 0)
            {
                return (
                    room.Id,
                    room.PvpResultPayload.ToArray(),
                    room.Members.Values.Count(member => member.Character is not null),
                    HasAllActiveArenaResultsLocked(room));
            }
        }

        var deadline = DateTime.UtcNow + ArenaResultCollectionTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_arenaRoomGate)
            {
                if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                    || !room.Members.ContainsKey(requester.SessionId))
                    return null;
                if (room.PvpResultPayload.Length != 0 || HasAllActiveArenaResultsLocked(room))
                    return BuildOrGetArenaResultLocked(room);
            }
            await Task.Delay(50, token);
        }

        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId))
                return null;
            return BuildOrGetArenaResultLocked(room);
        }
    }

    private static bool HasAllActiveArenaResultsLocked(ArenaRoom room) =>
        room.Members.Keys.All(sessionId =>
            room.EndingSessionIds.Contains(sessionId)
            && room.EndValuesBySession.ContainsKey(sessionId));

    private static (int RoomId, byte[] Payload, int RecordCount, bool AllActiveResultsReceived)
        BuildOrGetArenaResultLocked(ArenaRoom room)
    {
        var allResultsReceived = HasAllActiveArenaResultsLocked(room);
        if (room.PvpResultPayload.Length == 0)
        {
            var members = room.Members.Values
                .Where(member => member.Character is not null)
                .OrderBy(member => member.ArenaSlotIndex)
                .ToArray();
            var teamMode = members.Length > 1
                           && members.All(member => member.ArenaTeamCode is 1 or 2)
                           && members.Select(member => member.ArenaTeamCode).Distinct().Count() == 2;
            var teamValues = teamMode
                ? members
                    .GroupBy(member => member.ArenaTeamCode)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Aggregate(0UL, (sum, member) =>
                            sum + room.EndValuesBySession.GetValueOrDefault(member.SessionId)))
                : null;
            var records = members.Select(member =>
            {
                var value = room.EndValuesBySession.GetValueOrDefault(member.SessionId);
                bool won;
                if (teamMode)
                {
                    var ownTeamValue = teamValues!.GetValueOrDefault(member.ArenaTeamCode);
                    var otherTeamValue = teamValues!
                        .Where(pair => pair.Key != member.ArenaTeamCode)
                        .Select(pair => pair.Value)
                        .DefaultIfEmpty(ownTeamValue)
                        .Max();
                    won = ownTeamValue > otherTeamValue;
                }
                else
                {
                    var otherValue = members
                        .Where(other => other.SessionId != member.SessionId)
                        .Select(other => room.EndValuesBySession.GetValueOrDefault(other.SessionId))
                        .DefaultIfEmpty(0u)
                        .Max();
                    won = value > otherValue;
                }
                return BuildArenaPvpResultRecord(member.Character!, won ? (ushort)1 : (ushort)0);
            }).ToArray();
            room.PvpResultPayload = ArenaProtocol.BuildPvpResults(records);
        }
        return (
            room.Id,
            room.PvpResultPayload.ToArray(),
            room.Members.Values.Count(member => member.Character is not null),
            allResultsReceived);
    }

    private bool TryResetArenaRound(
        ConnectionSession requester,
        ReadOnlySpan<byte> requestPayload,
        out int roomId,
        out byte[] responsePayload,
        out bool roundReset)
    {
        roomId = 0;
        responsePayload = [];
        roundReset = false;
        var resetMode = BinaryPrimitives.ReadUInt16LittleEndian(requestPayload.Slice(2, 2));
        if (resetMode is not (1 or 2))
            return false;

        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId))
                return false;
            roomId = room.Id;
            if (room.Started)
            {
                if (!room.ResultSessionIds.Contains(requester.SessionId)
                    || room.PvpResultPayload.Length == 0)
                    return false;
                room.ResetSessionIds.Add(requester.SessionId);
                roundReset = TryCompleteArenaRoundResetLocked(room);
            }
            else if (!room.ResetSessionIds.Contains(requester.SessionId))
            {
                return false;
            }

            responsePayload = BuildArenaResettingPayload(requestPayload[0], requestPayload[1]);
            return true;
        }
    }

    private static bool TryCompleteArenaRoundResetLocked(ArenaRoom room)
    {
        if (!room.Started || !room.Members.Keys.All(room.ResetSessionIds.Contains))
            return false;

        room.Started = false;
        room.GameDataPayload = [];
        room.CurrentHpBySession.Clear();
        room.ScoreBySession.Clear();
        room.ClearedEntityRuntimeUids.Clear();
        room.ClaimedDrops.Clear();
        foreach (var member in room.Members.Values)
        {
            member.ArenaReady = false;
            member.ArenaTeamCode = GetDefaultArenaTeamCode(member.ArenaSlotIndex);
            ResetP2PState(member);
        }
        return true;
    }

    private bool TryStartArenaRoom(
        ConnectionSession requester,
        out string reason)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId))
            {
                reason = "room-not-found";
                return false;
            }
            if (room.OwnerSessionId != requester.SessionId)
            {
                reason = "requester-is-not-owner";
                return false;
            }
            if (room.Started)
            {
                reason = "already-started";
                return false;
            }
            if (room.Members.Count < 2)
            {
                reason = "not-enough-members";
                return false;
            }

            var guests = room.Members.Values
                .Where(member => member.SessionId != room.OwnerSessionId)
                .ToArray();
            if (guests.Any(member => !member.ArenaReady))
            {
                reason = "member-not-ready";
                return false;
            }

            if (room.GameDataPayload.Length != ArenaProtocol.GameDataResponseLength)
            {
                reason = "game-data-not-ready";
                return false;
            }

            var loadingMember = room.Members.Values.FirstOrDefault(member =>
                !member.P2PInfoRegistered || !member.ArenaP2PProtocolConfirmed);
            if (loadingMember is not null)
            {
                reason = $"member-loading:{loadingMember.Character?.Name ?? loadingMember.SessionId}";
                return false;
            }

            var teamCodes = room.Members.Values
                .Select(member => member.ArenaTeamCode)
                .ToArray();
            if (teamCodes.Any(team => team != 0)
                && (teamCodes.Any(team => team == 0)
                    || !teamCodes.Contains((byte)1)
                    || !teamCodes.Contains((byte)2)))
            {
                reason = "incomplete-team-selection";
                return false;
            }

            room.Started = true;
            room.EndingSessionIds.Clear();
            room.ResultSessionIds.Clear();
            room.ResetSessionIds.Clear();
            room.EndValuesBySession.Clear();
            room.PvpResultPayload = [];
            room.CurrentHpBySession.Clear();
            room.ScoreBySession.Clear();
            room.ClearedEntityRuntimeUids.Clear();
            room.ClaimedDrops.Clear();
            foreach (var member in room.Members.Values)
            {
                if (member.Character is null)
                    continue;
                room.CurrentHpBySession[member.SessionId] =
                    (ushort)Math.Clamp(member.Character.MaxHp, 1, ushort.MaxValue);
                room.ScoreBySession[member.SessionId] = 0;
            }
            reason = "ok";
            return true;
        }
    }

    private static ArenaPvpResultRecord BuildArenaPvpResultRecord(CharacterRecord character, ushort win)
    {
        var levelStart = CharacterProgression.ExperienceRequiredForLevel(character.Level);
        var nextLevel = character.Level >= CharacterProgression.MaximumLevel
            ? levelStart + 1
            : CharacterProgression.ExperienceRequiredForLevel(character.Level + 1);
        var protocolLevelStart = (uint)Math.Clamp(levelStart, 0L, uint.MaxValue - 1L);
        var protocolNextLevel = (uint)Math.Clamp(
            nextLevel,
            protocolLevelStart + 1L,
            uint.MaxValue);
        var protocolExperience = GetDungeonResultExperience(
            character.Experience,
            protocolLevelStart,
            protocolNextLevel);
        return new ArenaPvpResultRecord(
            GetSceneEntityId(character),
            win,
            0,
            0,
            checked((byte)Math.Clamp(character.Level, 1, byte.MaxValue)),
            1,
            0,
            0,
            0,
            protocolExperience,
            protocolLevelStart,
            protocolNextLevel,
            1,
            []);
    }

    private bool IsArenaEntityInRoom(ConnectionSession requester, uint uid)
    {
        lock (_arenaRoomGate)
        {
            return _arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                   && room.Members.ContainsKey(requester.SessionId)
                   && room.Members.Values.Any(member =>
                       member.Character is { } character
                       && GetSceneEntityId(character) == uid);
        }
    }

    private void QueueArenaDisconnectNotification(ConnectionSession member)
    {
        if (!member.AuxiliaryGameSession
            || member.Character is null
            || GetArenaRoom(member) is null)
            return;
        var owner = FindArenaRoomOwnerAfterLeave(member) ?? member.Character;
        QueueArenaBroadcast(
            member,
            0xCF74,
            BuildGameRoomLeavePayload(member.Character, owner),
            false,
            "arena connection leave");
        RemoveArenaRoomMember(member);
        QueueArenaLobbyRoomListRefresh(member, "arena connection left");
    }

    private (int ExpectedPeerCount, P2PPeerEndpoint[] Peers) GetArenaP2PPeers(
        ConnectionSession requester)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(requester.ArenaRoomId, out var room)
                || !room.Members.ContainsKey(requester.SessionId))
                return (0, []);
            var otherMembers = room.Members.Values
                .Where(member => member.SessionId != requester.SessionId)
                .OrderBy(member => member.ArenaSlotIndex)
                .ToArray();
            var peers = otherMembers
                .Where(member => member.Character is not null
                                 && member.P2PInfoRegistered
                                 && member.P2PIpAddress is not null
                                 && member.P2PPort != 0)
                .Select(member => new P2PPeerEndpoint(
                    member.ArenaSlotIndex,
                    GetSceneEntityId(member.Character!),
                    member.P2PIpAddress!,
                    member.P2PPort))
                .ToArray();
            return (otherMembers.Length, peers);
        }
    }

    private void QueueArenaBroadcast(
        ConnectionSession source,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        bool includeSource,
        string reason)
    {
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(source.ArenaRoomId, out var room))
                return;
            foreach (var target in room.Members.Values)
            {
                if (!includeSource && target.SessionId == source.SessionId)
                    continue;
                source.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
                    target, opcode, payload.ToArray(), reason));
            }
        }
    }

    private void QueueArenaLobbyRoomListRefresh(
        ConnectionSession source,
        string reason)
    {
        foreach (var target in _activeArenaSessions.Values)
        {
            if (target.SessionId == source.SessionId
                || !target.OnlineTracked
                || !target.AuxiliaryGameSession
                || target.ChannelId != source.ChannelId
                || target.ArenaGameType != source.ArenaGameType
                || target.ArenaRoomId != 0)
                continue;
            source.PendingSessionBroadcasts.Add(new PendingSessionBroadcast(
                target,
                0xCF12,
                BuildArenaRoomListPayload(target),
                reason));
        }
    }

    private static string GetArenaGameTypeText(byte gameType) => gameType switch
    {
        1 => "鍒濈骇",
        2 => "中级",
        3 => "楂樼骇",
        4 => "自由",
        _ => $"未知({gameType})"
    };

    private static int GetArenaGameAdapterPort(byte gameType)
    {
        if (gameType is < 1 or > ArenaGameAdapterTypeCount)
            throw new ArgumentOutOfRangeException(nameof(gameType));
        return ArenaGameAdapterFirstPort + gameType - 1;
    }

    private static byte GetArenaGameTypeFromPort(int port)
        => port >= ArenaGameAdapterFirstPort
            && port < ArenaGameAdapterFirstPort + ArenaGameAdapterTypeCount
                ? checked((byte)(port - ArenaGameAdapterFirstPort + 1))
                : (byte)0;

    private static byte[] BuildChannelConnectionPayload(CharacterRecord? character, bool accepted)
    {
        // C352 frame+9 is the guide-entry request flag. Persisted characters
        // must receive zero or the client starts the guide again.
        var payload = new byte[4];
        payload[0] = accepted ? (byte)0x64 : (byte)0;
        payload[1] = (byte)(character is { TutorialCompleted: true } ? 0 : 1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(2, 2),
            character is null ? (ushort)1 : GetSceneEntityId(character));
        return payload;
    }

    private static byte[] BuildLoadNecessityReadinessPayload()
    {
        var payload = new byte[4];
        // C594 is a fixed 12-byte packet. The client extracts exactly eight
        // one-bit scene readiness flags from this dword. Send it in response
        // to C353 so it cannot reuse the following C354/C355 control word.
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0xFFu);
        return payload;
    }

    internal static byte[] BuildLoadNecessityPayload(
        CharacterRecord? character,
        byte[] dungeonClearMasks,
        byte[] dungeonBestRatings,
        byte[] dungeonSecretBestRatings,
        CoupleRelationRecord? coupleRelation)
    {
        var payload = new byte[C355FrameLength - NativeHeaderLength];
        // The C355 consumer passes frame+13 to the client's pet-carry setter.
        // C44C restores the inventory/model, but this independent flag drives
        // village following and the "pet required" dungeon-entry check.
        payload[5] = GetEquippedPetItemCode(character) != 0 ? (byte)1 : (byte)0;
        // The retail C355 consumer restores the current-channel and global
        // mike counters from frame+14 and frame+15 respectively.
        payload[6] = character?.MikeChannelUseCount ?? 0;
        payload[7] = character?.MikeGlobalUseCount ?? 0;
        // The retail C355 consumer restores its town/page globals from
        // frame+32/frame+33 before constructing the first C367 request. Every
        // fresh process also promotes this first C355 into FirstVillageFlag=1;
        // that actor path ignores C368 coordinates and uses the loaded page's
        // built-in first-entry point. Keep completed characters on the proven
        // 0/0 bootstrap tuple instead of restoring an arbitrary saved page that
        // can yield CVillageChar(-1,-1). Normal C365/C367/CB21 movement remains
        // persistent after this bootstrap actor exists.
        if (character is { TutorialCompleted: true })
        {
            payload[24] = TownPositionPolicy.LoginBootstrapMapId;
            payload[25] = TownPositionPolicy.LoginBootstrapTownPage;
        }
        // The C355 consumer reads frame+35 and calls sub_40FEE8, whose
        // implementation (sub_A72570) stores this value at local-character
        // state +2. That is the same ushort read by the apartment level gate.
        var level = Math.Clamp(character?.Level ?? 1, 1, CharacterProgression.MaximumLevel);
        payload[27] = (byte)level;
        // C355 frame+0x24 restores the persisted dungeon grade used by the
        // title renderer. The launcher fixed selection and native CF88 state
        // share this same managed snapshot.
        payload[0x24 - NativeHeaderLength] = character?.DungeonGrade ?? 0;
        // The retail C355 handler stores frame+40/+44/+48 as accumulated
        // experience, this level's start, and the next-level threshold. The
        // profile window reads those same three local-state values to compute
        // (current - start) / (next - start).
        var levelStart = CharacterProgression.ExperienceRequiredForLevel(level);
        var nextLevel = level >= CharacterProgression.MaximumLevel
            ? levelStart + 1
            : CharacterProgression.ExperienceRequiredForLevel(level + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(32, 4),
            (uint)Math.Clamp(character?.Experience ?? 0L, 0L, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(36, 4),
            (uint)Math.Clamp(levelStart, 0L, uint.MaxValue - 1L));
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(40, 4),
            (uint)Math.Clamp(nextLevel, levelStart + 1, uint.MaxValue));
        // C355 frame+0x38 restores the selected PET code independently
        // from the frame+0x0D carry flag.
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(0x38 - 8, 4),
            GetEquippedPetItemCode(character));
        // C355 frame+0x3C contains 20 episodes x 3 difficulty bytes. Each byte
        // stores Dungeon 1/2/3/Super-BOSS in bits 0/1/2/3.
        dungeonClearMasks.AsSpan(0, Math.Min(dungeonClearMasks.Length, 60))
            .CopyTo(payload.AsSpan(0x3C - 8, 60));
        // The separate full-frame +0x80..+0x87 village prerequisite QWORD
        // is applied after every profile field has been serialized. The score
        // board is a different cached domain: +0x88..+0xC3 contains the
        // 20-episode x 3-difficulty ordinary table, and +0xC4..+0xC7 contains
        // four secret-episode bytes. Each byte packs four two-bit best ranks
        // (0=none, 1=B, 2=A, 3=S). +0xC8..+0xDE is a separate 23-row frontier
        // table and remains zero until that progression domain is persisted.
        dungeonBestRatings.AsSpan(0, Math.Min(dungeonBestRatings.Length, C355DungeonRatingsLength))
            .CopyTo(payload.AsSpan(C355DungeonRatingsFrameOffset - NativeHeaderLength, C355DungeonRatingsLength));
        dungeonSecretBestRatings.AsSpan(0, Math.Min(dungeonSecretBestRatings.Length, C355SecretRatingsLength))
            .CopyTo(payload.AsSpan(C355SecretRatingsFrameOffset - NativeHeaderLength, C355SecretRatingsLength));
        // The retail C355 consumer restores the partner name from frame+0xDF
        // and the short ring suffix from frame+0xF0. The native emotion-page
        // gate is name-derived: an empty slot blocks the ring lookup entirely.
        // Serialize a nonempty name only with a catalog-backed 43,000,000+N
        // ring so the unmodified client can resolve exactly that record. Any
        // stale/corrupt relation fails closed to empty-name/zero-ring state.
        WriteC355CoupleState(payload, character, coupleRelation);
        // The retail C355 consumer reads frame+0xF2 as the persisted revival
        // item counter and stores it in the local character state at +0x5600.
        payload[C355RevivalCountFrameOffset - NativeHeaderLength] =
            character?.RevivalUseCount ?? 0;

        // Keep access carriers independent from the cached score-board ranks.
        // The final writer repeats the idempotent access normalization before checksum.
        NormalizeC355VillageAccessPayload(payload);
        return payload;
    }

    internal static void NormalizeC355VillageAccessFrame(Span<byte> frame)
    {
        if (frame.Length != C355FrameLength
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) != 0xC355)
            return;

        NormalizeC355VillageAccessPayload(frame[NativeHeaderLength..]);
    }

    internal static void NormalizeC355VillageAccessPayload(Span<byte> payload)
    {
        if (payload.Length != C355FrameLength - NativeHeaderLength)
            return;

        // sub_544000 copies full-frame +0x3C..+0x77 into the ordinary
        // dungeon availability object consumed by sub_509BC0. reference implementation's final
        // all-open payload used 0x0F in every one of these 60 cells.
        payload.Slice(
            C355DungeonClearFrameOffset - NativeHeaderLength,
            C355DungeonClearLength).Fill(0x0F);

        // Full-frame +0x80/+0x84 is a separate QWORD consumed by
        // sub_54EDD0 and sub_509EC0. Set only the 22 predecessor pairs; retain
        // bit44..63 exactly, including the not-implied final-clear pair.
        var prerequisiteOffset = C355VillagePrerequisiteFrameOffset - NativeHeaderLength;
        var prerequisites = BinaryPrimitives.ReadUInt64LittleEndian(
            payload.Slice(prerequisiteOffset, C355VillagePrerequisiteLength));
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.Slice(prerequisiteOffset, C355VillagePrerequisiteLength),
            prerequisites | C355VillagePrerequisiteLow44Mask);

        // Do not normalize +0x88..<+0xDF. Those bytes are the score-board
        // cache consumed by the ordinary, secret, and frontier "view credits"
        // pages. Replacing them with packed state 1 fabricates a B rank for
        // every slot and destroys the persisted best result. Access remains
        // carried by +0x3C..+0x77 and the independent prerequisite QWORD.
    }

    private static void WriteC355CoupleState(
        Span<byte> payload,
        CharacterRecord? character,
        CoupleRelationRecord? coupleRelation)
    {
        var partnerNameField = payload.Slice(
            C355PartnerNameFrameOffset - NativeHeaderLength,
            C355PartnerNameLength);
        var ringSuffixField = payload.Slice(
            C355RingSuffixFrameOffset - NativeHeaderLength,
            2);
        partnerNameField.Clear();
        ringSuffixField.Clear();

        var partnerName = character is null || coupleRelation is null
            ? string.Empty
            : coupleRelation.GetPartnerName(character.Id);
        var ringItemCode = coupleRelation?.RingItemCode ?? 0;
        if (string.IsNullOrWhiteSpace(partnerName)
            || ringItemCode <= CoupleRingCodeBase
            || ringItemCode - CoupleRingCodeBase > ushort.MaxValue
            || !ShopCatalog.TryGet(ringItemCode, out var ring)
            || ring.Category != 43
            || !string.Equals(ring.Source, CoupleRingCatalogSource, StringComparison.Ordinal))
            return;

        WriteFixedGbk(partnerNameField, partnerName);
        BinaryPrimitives.WriteUInt16LittleEndian(
            ringSuffixField,
            checked((ushort)(ringItemCode - CoupleRingCodeBase)));
    }

    private static byte[] BuildClientDungeonClearMasks(byte[] persistedMasks)
        => BuildClientDungeonDifficultyTable(persistedMasks);

    private static byte[] BuildClientDungeonBestRatings(byte[] persistedRatings)
        => BuildClientDungeonDifficultyTable(persistedRatings);

    private static byte[] BuildClientDungeonDifficultyTable(byte[] persistedTable)
    {
        // C355 stores 3 * Episode + selector. Persistence uses logical
        // low/middle/high, while the retail normal-stage selectors are 2/0/1.
        var clientTable = new byte[DungeonEpisodeCount * DungeonDifficultyCount];
        for (byte episode = 0; episode < DungeonEpisodeCount; episode++)
        {
            for (byte logicalDifficulty = 0;
                 logicalDifficulty < DungeonDifficultyCount;
                 logicalDifficulty++)
            {
                var persistedIndex = episode * DungeonDifficultyCount + logicalDifficulty;
                if (persistedIndex >= persistedTable.Length)
                    continue;
                var clientSelector = EncodeDungeonDifficultySelector(
                    logicalDifficulty,
                    superBoss: false);
                clientTable[episode * DungeonDifficultyCount + clientSelector] =
                    persistedTable[persistedIndex];
            }
        }
        return clientTable;
    }

    private static bool IsClientDungeonCleared(byte clearMask, int dungeon) =>
        dungeon switch
        {
            0 => (clearMask & 0x01) != 0,
            1 => (clearMask & 0x02) != 0,
            2 => (clearMask & 0x0C) == 0x0C,
            _ => false
        };

    internal static byte[] BuildCardListPayload(
        byte[] requestPayload,
        IReadOnlyList<CharacterCardRecord> ownedCards,
        CharacterRecord character,
        IReadOnlyList<CharacterSkillRecord> learnedSkills,
        DateTime? currentTime = null)
    {
        var payload = new byte[CardListResponsePayloadLength];
        requestPayload.AsSpan(0, Math.Min(requestPayload.Length, CardListRequestPayloadLength))
            .CopyTo(payload);
        if (requestPayload.Length != CardListRequestPayloadLength)
            return payload;

        var mode = BinaryPrimitives.ReadUInt16LittleEndian(requestPayload.AsSpan(0, 2));
        var category = requestPayload[2];
        var page = requestPayload[3];
        var pageSize = category == 3 ? 10 : 20;
        foreach (var card in ownedCards)
        {
            if (card.Category != category || card.Page != page || card.Slot >= pageSize)
                continue;

            // The common card-book renderer reads frame+12. The mode-40
            // special-card branch additionally reads its ten counts at +52.
            payload[4 + card.Slot] = card.Quantity;
            if (mode == 40 && category == 3)
                payload[44 + card.Slot] = card.Quantity;
        }

        if (mode == 40 && category == 3)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(68, 4),
                SkillSlotExpansionTime.Encode(currentTime ?? DateTime.Now));
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(72, 2), character.SkillPoints);
            var validSkills = learnedSkills
                .Where(skill => skill.Grade is >= 1 and <= 5
                    && SkillCatalog.TryGet(skill.SkillCode, out _))
                .OrderBy(skill => skill.SkillCode)
                .ToArray();
            var skillGrades = validSkills.ToDictionary(skill => skill.SkillCode, skill => skill.Grade);
            var selectedSkill0 = skillGrades.ContainsKey(character.SelectedSkill0)
                ? character.SelectedSkill0
                : 0u;
            var selectedSkill1 = skillGrades.ContainsKey(character.SelectedSkill1)
                ? character.SelectedSkill1
                : 0u;

            // The mode-40 C3E8 consumer passes frame+84 as a two-entry grade
            // array (one byte every two bytes) and frame+88 as two uint skill
            // codes. C402 uses a different, packed grade layout.
            payload[76] = skillGrades.GetValueOrDefault(selectedSkill0);
            payload[78] = skillGrades.GetValueOrDefault(selectedSkill1);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(80, 4), selectedSkill0);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(84, 4), selectedSkill1);

            // sub_879290 builds a lookup map only for the requested zero-based
            // skill-family page. The C3E8 mode-40 consumer then looks up every
            // non-zero learned code in that page map without a null check.
            // Publishing another family's code therefore crashes at
            // sub_8795A0+0x3A when the user changes skill pages. The selected
            // Z/X slots above are global and intentionally remain unfiltered.
            var requestedSkillFamily = page is >= 1 and <= 2
                ? page - 1
                : -1;
            var pageSkills = validSkills
                .Where(skill => SkillCatalog.TryGet(skill.SkillCode, out var catalogSkill)
                    && catalogSkill.SkillFamily == requestedSkillFamily)
                .Take(7)
                .ToArray();
            for (var index = 0; index < pageSkills.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    payload.AsSpan(88 + index * sizeof(uint), sizeof(uint)),
                    pageSkills[index].SkillCode);
                payload[116 + index] = pageSkills[index].Grade;
            }
            var skillSlotExpiration = character.SkillSlotExpansionExpires != 0
                ? character.SkillSlotExpansionExpires
                : selectedSkill1 != 0
                    ? 2_099_123_123u
                    : 0u;
            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(124, 4),
                skillSlotExpiration);
        }

        // Every C3E8 mode refreshes the counted-key widgets and the free-key
        // entitlement. These are full-frame +0x40..+0x43 and +0x88.
        payload[56] = character.CardSummonCount;
        payload[57] = character.CardGoldenKeyCount;
        payload[58] = character.CardMysteryKeyCount;
        payload[59] = 0; // retained fourth/legacy key field
        var nowWire = SkillSlotExpansionTime.Encode(currentTime ?? DateTime.Now);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(128, 2),
            character.FreeMagicExpansionExpires != 0 && nowWire < character.FreeMagicExpansionExpires
                ? (ushort)1
                : (ushort)0);
        return payload;
    }

    private static byte[] BuildSkillUpgradeResultPayload(
        ushort result,
        ushort remainingSkillPoints,
        uint skillCode)
    {
        var payload = new byte[SkillUpgradeResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), result);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), remainingSkillPoints);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), skillCode);
        return payload;
    }

    private static byte[] BuildSkillSlotResultPayload(
        ushort result,
        uint skill0,
        byte grade0,
        uint skill1,
        byte grade1)
    {
        var payload = new byte[SkillSlotResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), result);
        payload[2] = grade0;
        payload[3] = grade1;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), skill0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), skill1);
        return payload;
    }

    private static byte[] BuildDungeonSkillUseResultPayload(
        ushort playerEntityUid,
        byte result,
        byte skillGrade,
        uint skillCode)
    {
        var payload = new byte[DungeonSkillUseResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), playerEntityUid);
        payload[2] = result;
        payload[3] = skillGrade;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), skillCode);
        return payload;
    }

    private static byte[] BuildDungeonQuickItemUsePayload(
        ushort playerEntityUid,
        ushort quickSlot,
        uint itemCode,
        ushort hpRestored,
        ushort mpRestored)
    {
        var payload = new byte[DungeonQuickItemUseResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), playerEntityUid);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), quickSlot);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), itemCode);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), hpRestored);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10, 2), mpRestored);
        return payload;
    }

    internal static bool TryParseInventoryExpansionRequest(
        ReadOnlySpan<byte> payload,
        out byte expansionType,
        out ushort reserved)
    {
        expansionType = 0;
        reserved = 0;
        if (payload.Length != InventoryExpansionRequestPayloadLength)
            return false;
        var rawType = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        reserved = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        if (rawType > byte.MaxValue)
            return false;
        expansionType = checked((byte)rawType);
        return true;
    }

    private static byte[] BuildSkillSlotExpansionResultPayload(
        byte result,
        byte expansionType,
        byte inventorySlot,
        uint itemCode,
        uint expiration)
    {
        // C481 is consumed as result, reserved, type, slot, item and the
        // YYYYMMDDHH expiration value. Only result zero removes the ticket
        // and enables the extra skill slot.
        var payload = new byte[InventoryExpansionResponsePayloadLength];
        payload[0] = result;
        payload[2] = expansionType;
        payload[3] = inventorySlot;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), itemCode);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), expiration);
        return payload;
    }

    private static byte[] BuildFaceCouponResultPayload(
        bool success,
        ReadOnlySpan<byte> appearance)
    {
        var payload = new byte[FaceCouponResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(0, 2),
            success ? (ushort)1000 : (ushort)0);
        if (success && appearance.Length == 36)
            appearance.CopyTo(payload.AsSpan(8, 36));
        return payload;
    }

    private static byte[] BuildGameConnectionPayload()
    {
        var payload = new byte[4];
        // The CF0A consumer treats 1000 as SUCCESS_GAME_CONNECTION.
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1000u);
        return payload;
    }

    private static byte[] BuildP2PMyInfoPayload(CharacterRecord character, byte slotIndex)
    {
        var payload = new byte[4];
        payload[0] = 1;
        payload[1] = slotIndex;
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(2, 2),
            (ushort)Math.Clamp(character.Id, 1L, (long)ushort.MaxValue));
        return payload;
    }

    private static byte[] BuildGameRoomLocalIdentityPayload(CharacterRecord character)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload,
            (ushort)Math.Clamp(character.Id, 1L, (long)ushort.MaxValue));
        return payload;
    }

    private byte[] BuildDungeonRoomListPayload(ConnectionSession requester)
    {
        lock (_dungeonRoomGate)
        {
            var rooms = _dungeonRooms.Values
                .Where(room => room.ChannelId == requester.ChannelId
                               && !room.Started
                               && !room.HasPendingTransition)
                .Where(room => room.Members.ContainsKey(room.OwnerSessionId))
                .OrderBy(room => room.Id)
                .Take(12)
                .ToArray();
            var payload = new byte[4 + rooms.Length * 40];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)rooms.Length);
            for (var index = 0; index < rooms.Length; index++)
            {
                var room = rooms[index];
                var record = payload.AsSpan(4 + index * 40, 40);
                room.CreateRequestPayload.AsSpan(0, Math.Min(24, room.CreateRequestPayload.Length))
                    .CopyTo(record.Slice(0, 24));
                if (record[0] == 0
                    && room.Members.TryGetValue(room.OwnerSessionId, out var owner)
                    && owner.Session.Character is not null)
                    WriteFixedGbk(record.Slice(0, 24), owner.Session.Character.Name);
                record[24] = 10;
                record[25] = 100;
                BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(26, 2), DecodeDungeonDifficulty(room));
                if (room.Members.TryGetValue(room.OwnerSessionId, out var listOwner)
                    && listOwner.Session.Character is not null)
                    WriteFixedGbk(record.Slice(28, 8), listOwner.Session.Character.Name);
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(36, 4), (uint)room.Id);
            }
            return payload;
        }
    }

    private static byte[] BuildDungeonGameplayEnterPayload(
        DungeonRoom room,
        ConnectionSession member)
    {
        // CF6F's success consumer reads result at frame+8, two room bytes at
        // +10/+11, a room value at +12, slot states at +16..+18, and two
        // in-frame strings beginning at +20 and +28.
        var payload = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 10);
        payload[2] = 0;
        payload[3] = member.DungeonSlotIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)room.Id);
        BuildDungeonEffectiveSlotStates(room).CopyTo(payload, 8);
        room.CreateRequestPayload.AsSpan(0, Math.Min(24, room.CreateRequestPayload.Length))
            .CopyTo(payload.AsSpan(12, 24));
        return payload;
    }

    private static byte[] BuildDungeonQuickEnterPayload(DungeonRoom? room, ushort result)
    {
        var payload = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), result);
        if (room is null)
            return payload;
        payload[2] = room.Episode;
        payload[3] = room.Dungeon;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)room.Id);
        BuildDungeonEffectiveSlotStates(room).CopyTo(payload, 8);
        if (room.Members.TryGetValue(room.OwnerSessionId, out var owner)
            && owner.Session.Character is { } ownerCharacter)
            WriteFixedGbk(payload.AsSpan(12, 8), ownerCharacter.Name);
        room.CreateRequestPayload.AsSpan(0, 8).CopyTo(payload.AsSpan(20, 8));
        return payload;
    }

    private static byte[] BuildArenaLobbyInfoPayload(CharacterRecord character, byte gameType)
    {
        // The retail CF0E consumer reads exactly 28 payload bytes at fixed
        // offsets: entity uid, level, arena-rank icon, HP/MP pairs, then two
        // current/maximum progress pairs. gameType belongs to C389 and must
        // never be written into the level byte here.
        var payload = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), GetSceneEntityId(character));
        _ = gameType;
        payload[2] = (byte)Math.Clamp(character.Level, 1, byte.MaxValue);
        payload[3] = 1; // first valid qz_inter_lv_icon resource index
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), (ushort)Math.Clamp(character.CurrentHp, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), (ushort)Math.Clamp(character.MaxHp, 1, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), (ushort)Math.Clamp(character.CurrentMp, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10, 2), (ushort)Math.Clamp(character.MaxMp, 1, ushort.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24, 4), 1);
        return payload;
    }

    private byte[] BuildArenaRoomListPayload(ConnectionSession requester)
    {
        ArenaRoomListEntry[] rooms;
        lock (_arenaRoomGate)
        {
            rooms = _arenaRooms.Values
                .Where(room => room.ChannelId == requester.ChannelId
                    && room.GameType == requester.ArenaGameType)
                .OrderBy(room => room.Id)
                .Take(100)
                .Select(room => new ArenaRoomListEntry(
                    checked((ushort)room.Id),
                    room.Title,
                    room.Password,
                    room.CreateRequest.Metadata,
                    checked((byte)room.Members.Count),
                    room.Started))
                .ToArray();
        }
        return ArenaProtocol.BuildRoomList(rooms);
    }

    private static byte[] BuildEmptyArenaRankingPayload(uint rankingType)
    {
        // The client unconditionally reads ten 36-byte records after type.
        var payload = new byte[4 + 10 * 36];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), rankingType);
        return payload;
    }

    private static byte[] BuildArenaCreateGamePayload(ArenaRoom room, CharacterRecord owner)
    {
        var payload = new byte[36];
        payload[0] = 10;
        // CF6D frame+9 is the room-owner flag. The same client setter receives
        // 1 for CF78 result 100 (created) and 0 for result 10 (joined).
        payload[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), checked((ushort)room.Id));
        room.SlotStates.CopyTo(payload, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), checked((uint)owner.Id));
        WriteFixedGbk(payload.AsSpan(12, 24), owner.Name);
        return payload;
    }

    private static byte[] BuildArenaLobbyEnterPayload(ArenaRoom room, CharacterRecord owner)
    {
        // The CF76 lobby consumer gates on the result byte. The remaining
        // fixed record mirrors the room-list identity fields used to enter it.
        var payload = new byte[64];
        payload[0] = 10;
        payload[1] = room.GameType;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), checked((ushort)room.Id));
        WriteFixedGbk(payload.AsSpan(4, 24), room.Title);
        WriteFixedGbk(payload.AsSpan(28, 8), room.Password);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(36, 4), checked((uint)owner.Id));
        WriteFixedGbk(payload.AsSpan(40, 24), owner.Name);
        return payload;
    }

    private static byte[] BuildArenaLobbyEnterFailurePayload() => new byte[64];

    private static byte[] BuildArenaGameplayEnterPayload(ArenaRoom room, ConnectionSession member)
    {
        // The retail CF6F consumer reads result, game type, local slot, room id,
        // three guest-slot states and the room title at these fixed offsets.
        var payload = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 10);
        payload[2] = room.GameType;
        payload[3] = member.ArenaSlotIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), checked((uint)room.Id));
        BuildArenaEffectiveSlotStates(room).CopyTo(payload, 8);
        WriteFixedGbk(payload.AsSpan(12, 24), room.Title);
        return payload;
    }

    private static byte[] BuildArenaEffectiveSlotStates(ArenaRoom room)
    {
        var states = room.SlotStates.ToArray();
        foreach (var roomMember in room.Members.Values)
        {
            // CF6D/CF78 carry only the three guest controls. ArenaSlotIndex 0
            // is the owner, while member slots 1..3 map to guest controls 0..2.
            if (roomMember.ArenaSlotIndex is >= 1 and <= 3)
                states[roomMember.ArenaSlotIndex - 1] = 2;
        }
        return states;
    }

    private static byte[] BuildArenaQuickEnterPayload(
        ArenaRoom room,
        ushort result)
        => ArenaProtocol.BuildQuickEnterResponse(
            result,
            checked((ushort)room.Id),
            room.ResponseField2,
            room.ResponseField3,
            BuildArenaEffectiveSlotStates(room),
            room.Password,
            room.Title);

    private static byte[] BuildArenaQuickEnterFailurePayload()
        => ArenaProtocol.BuildQuickEnterResponse(50, 0, 0, 0, [0, 0, 0], string.Empty, string.Empty);

    private static byte[] BuildDungeonGameplayEnterFailurePayload()
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 20);
        return payload;
    }

    private static byte[] BuildDungeonEffectiveSlotStates(DungeonRoom room)
    {
        // The room-entry consumers use state 2 for occupied slots and state 1/0
        // for the host's closed/open empty-slot switch. CF71 then supplies the
        // entity assigned to each occupied slot.
        var states = room.SlotStates.ToArray();
        foreach (var member in room.Members.Values)
        {
            if (member.SlotIndex < states.Length)
                states[member.SlotIndex] = 2;
        }
        return states;
    }

    private static byte[] BuildGameRoomSlotChangePayload(ushort slotIndex, byte state)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), slotIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), state);
        return payload;
    }

    private static byte[] BuildGameReadyPayload(CharacterRecord character, bool ready, byte teamCode)
    {
        // CF7E is consumed as UID at frame+8, ready at +10 and team at +11.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(0, 2),
            (ushort)Math.Clamp(character.Id, 1L, (long)ushort.MaxValue));
        payload[2] = ready ? (byte)1 : (byte)0;
        payload[3] = teamCode;
        return payload;
    }

    private static byte[] BuildP2POtherInfoPayload(IReadOnlyList<P2PPeerEndpoint> peers)
    {
        var payload = new byte[4 + peers.Count * P2PPeerRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 400);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), (ushort)peers.Count);
        for (var index = 0; index < peers.Count; index++)
        {
            var peer = peers[index];
            var record = payload.AsSpan(4 + index * P2PPeerRecordLength, P2PPeerRecordLength);
            // Retail treats this byte as the zero-based index in this response,
            // not as the member's room slot. Using the room slot prevents the
            // clients from matching the only peer and triggers CFD7 timeouts.
            record[1] = checked((byte)index);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(2, 2), peer.CharacterUid);
            Encoding.ASCII.GetBytes(peer.IpAddress, record.Slice(4, P2PIpAddressLength - 1));
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(20, 4), peer.Port);
        }
        return payload;
    }

    private static bool TryParseP2PMyInfo(
        ReadOnlySpan<byte> payload,
        out string ipAddress,
        out ushort port)
    {
        ipAddress = string.Empty;
        port = 0;
        if (payload.Length != P2PMyInfoRequestPayloadLength)
            return false;

        var addressSlot = payload[..P2PIpAddressLength];
        var terminator = addressSlot.IndexOf((byte)0);
        if (terminator <= 0)
            return false;
        var addressText = Encoding.ASCII.GetString(addressSlot[..terminator]);
        var rawPort = BinaryPrimitives.ReadUInt32LittleEndian(payload[P2PIpAddressLength..]);
        if (!IPAddress.TryParse(addressText, out var parsedAddress)
            || parsedAddress.AddressFamily != AddressFamily.InterNetwork
            || rawPort is 0 or > ushort.MaxValue)
            return false;

        ipAddress = parsedAddress.ToString();
        port = (ushort)rawPort;
        return true;
    }

    private static byte[] BuildP2PProtocolPayload()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 20u);
        return payload;
    }

    private static byte[] BuildMulticastingGameEventPayload(CharacterRecord character)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload,
            (uint)Math.Clamp(character.Id, 1L, (long)uint.MaxValue));
        return payload;
    }

    private static byte[] BuildFlyshootingGameDataPayload()
        => BuildFlyshootingGameDataPayload(0, 0, 0);

    private static byte[] BuildFlyshootingGameDataPayload(
        byte stageIndex,
        byte showStageNumber,
        ushort mapIndex)
        => DungeonProtocol.BuildGameData(stageIndex, showStageNumber, mapIndex, cardEpisode: 0);

    private static byte[] BuildDungeonCollisionResponsePayload(
        ReadOnlySpan<byte> requestPayload,
        uint npcUid)
    {
        var payload = new byte[40];
        requestPayload[..Math.Min(requestPayload.Length, DungeonCollisionPayloadLength)].CopyTo(payload);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(0x10, 2),
            (ushort)Math.Clamp(npcUid, 1u, (uint)ushort.MaxValue));
        payload[0x12] = 200;
        return payload;
    }

    private static byte[] BuildDungeonBossRecordingPayload(
        IReadOnlyList<uint> hitScores,
        uint bossEnergy,
        IReadOnlyList<byte> stageStates,
        IReadOnlyList<byte> clearRanks,
        IReadOnlyList<ushort> hpSteals,
        IReadOnlyList<ushort> mpSteals,
        byte bossObjectIndex,
        byte bossParentIndex,
        ushort bossComponentIndex,
        uint immediateHansReward,
        uint dropItemCode)
    {
        return DungeonProtocol.BuildBoss(
            hitScores,
            bossEnergy,
            stageStates.ToArray(),
            clearRanks.ToArray(),
            hpSteals,
            mpSteals,
            bossObjectIndex,
            bossParentIndex,
            bossComponentIndex,
            immediateHansReward,
            dropItemCode);
    }

    private static byte[] BuildDungeonPickupPayload(
        CharacterRecord beneficiary,
        CharacterRecord? owner,
        ushort pickupType,
        ushort dropUid,
        uint value)
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), GetSceneEntityId(beneficiary));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2),
            owner is null ? GetSceneEntityId(beneficiary) : GetSceneEntityId(owner));
        // D035 frame+0x0C is the original scene pickup type. In particular,
        // type 40 selects the client's skill/HP/MP upgrade-item branch.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), pickupType);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), dropUid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), value);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(14, 2),
            (ushort)Math.Clamp(beneficiary.CurrentHp, 0, ushort.MaxValue));
        return payload;
    }

    private static byte[] BuildDungeonEndGamePayload(
        CharacterRecord owner,
        CharacterRecord player,
        uint gainedExperience,
        long levelStart,
        long nextLevel,
        int hitScore,
        byte clearRating,
        int petLevelUpState = 0,
        int bonusScore = 0,
        int earnedHans = 0,
        int playerLevelUpState = 0)
    {
        var payload = new byte[56];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), GetSceneEntityId(owner));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), GetSceneEntityId(player));
        // CF88's native result record stores the character and pet level-up
        // booleans at +0x08/+0x09. The pet flag also gates the remote entity's
        // post-battle pet-level setter.
        payload[0x08] = playerLevelUpState == 0 ? (byte)0 : (byte)1;
        payload[0x09] = petLevelUpState == 0 ? (byte)0 : (byte)1;
        var petState = PetProgression.GetState(player, GetEquippedPetItemCode(player));
        payload[0x0C] = (byte)Math.Min(petState.Level, byte.MaxValue);
        payload[0x0D] = (byte)Math.Min(
            PetProgression.GetCurrentStageMaximumLevel(petState),
            byte.MaxValue);
        // Native CF88 consumes record+0x09 as the same ten-level-band icon
        // used by the room/player state packets. record+0x0C below is the
        // separate post-battle character level.
        payload[0x0B] = (byte)GetDungeonLevelIcon(player.Level);
        // Native CF88 record+0x0C is the player's post-battle level. The client
        // writes it straight back to the local character before drawing both
        // the settlement level and the following dungeon HUD.
        payload[0x0E] = (byte)Math.Clamp(player.Level, 1, byte.MaxValue);
        payload[0x0F] = clearRating;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x10, 4), gainedExperience);
        // CF88 uses cumulative experience/current-level start/next-level start.
        // Clamp inconsistent admin-edited archives into that absolute interval;
        // otherwise the client's unsigned percentage calculation can underflow.
        var protocolLevelStart = (uint)Math.Clamp(levelStart, 0L, uint.MaxValue - 1L);
        var protocolNextLevel = (uint)Math.Clamp(
            nextLevel,
            protocolLevelStart + 1L,
            uint.MaxValue);
        var protocolExperience = GetDungeonResultExperience(
            player.Experience,
            protocolLevelStart,
            protocolNextLevel);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x14, 4), protocolExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x18, 4), protocolLevelStart);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x1C, 4), protocolNextLevel);
        var safeHit = (uint)Math.Max(0, hitScore);
        var safeBonus = (uint)Math.Max(0, bonusScore);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x20, 4), safeHit + safeBonus);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x24, 4), petState.Experience);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x28, 4), safeHit);
        // Native frame +0x34 (payload +0x2C) is the result page's BonusScore;
        // frame +0x38 is TotalScore. Earned Hans is delivered at BOSS clear by
        // D012 frame +0x1C and persisted by CF87 without occupying a CF88 slot.
        _ = earnedHans;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x2C, 4), safeBonus);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x30, 4), safeHit + safeBonus);
        // +0x34 is not the native bPetLevelUp field. Its exact retail meaning
        // remains unclosed, so keep the reserved result-side value zero.
        return payload;
    }

    private static uint GetDungeonResultExperience(
        long experience,
        uint levelStart,
        uint nextLevel) =>
        (uint)Math.Clamp(experience, (long)levelStart, (long)nextLevel);

    private static bool IsDungeonSuperBoss(byte dungeon, byte realStage) =>
        dungeon == DungeonCountPerEpisode - 1 && realStage == 1;

    private static byte DecodeDungeonLogicalDifficulty(
        byte dungeon,
        byte realStage,
        ushort wireDifficulty)
    {
        if (wireDifficulty >= DungeonDifficultyCount)
            return byte.MaxValue;

        // The original client does not use one numeric domain for both jobs.
        // Normal stages select low/middle/high as 2/0/1. Super-BOSS result
        // stages use 0/1/2 so the ending UI can offer middle/top/next.
        if (IsDungeonSuperBoss(dungeon, realStage))
            return checked((byte)wireDifficulty);
        return wireDifficulty switch
        {
            2 => 0,
            0 => 1,
            1 => 2,
            _ => byte.MaxValue
        };
    }

    private static byte EncodeDungeonDifficultySelector(
        byte logicalDifficulty,
        bool superBoss)
    {
        if (logicalDifficulty >= DungeonDifficultyCount)
            throw new ArgumentOutOfRangeException(nameof(logicalDifficulty));
        if (superBoss)
            return logicalDifficulty;
        return logicalDifficulty switch
        {
            0 => 2,
            1 => 0,
            2 => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(logicalDifficulty))
        };
    }

    private static byte[] BuildDungeonResettingPayload(
        byte realStage,
        byte showStage,
        byte difficulty,
        byte dungeonSelector)
    {
        var payload = new byte[40];
        payload[0x20] = realStage;
        payload[0x21] = showStage;
        // The retail CF8C consumer stores native frame+0x2C in the same
        // +0x1E528 global used by CF6C's 16-bit difficulty selector.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x24, 2), difficulty);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x26, 2), dungeonSelector);
        return payload;
    }

    private static byte[] BuildArenaResettingPayload(byte realStage, byte showStage)
    {
        var payload = new byte[ArenaResettingResponsePayloadLength];
        payload[0x20] = realStage;
        payload[0x21] = showStage;
        return payload;
    }

    private static byte[] BuildDungeonStageRecordsPayload(
        byte[] requestPayload,
        IReadOnlyList<DungeonStageLeaderboardRecord> records)
    {
        // CF16 is a 252-byte frame: the echoed four-byte CF15 selector plus
        // ten fixed 24-byte records. The client consumes name[16], score u32,
        // character level u16 and the 1..7 level-icon band u16.
        var payload = new byte[244];
        requestPayload.AsSpan(0, Math.Min(requestPayload.Length, 4)).CopyTo(payload);
        for (var index = 0; index < Math.Min(records.Count, 10); index++)
        {
            var record = records[index];
            var offset = 4 + index * 0x18;
            WriteFixedGbk(payload.AsSpan(offset, 16), record.CharacterName);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 16, 4), record.BestScore);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 20, 2), record.CharacterLevel);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 22, 2),
                GetDungeonLevelIcon(record.CharacterLevel));
        }
        return payload;
    }

    private static ushort GetDungeonLevelIcon(int level) =>
        (ushort)Math.Clamp((Math.Max(1, level) - 1) / 10 + 1, 1, ClientMaximumLevelIcon);

    private static byte[] BuildUserHpMpAutoHealingPayload(CharacterRecord character)
    {
        // Retail opcode D8FF is USER_HP_MP_AUTO_HEALING in town and
        // EVENT_USER_HP_MP_AUTO_HEALING in a dungeon. Both consumers read
        // max HP/MP and current HP/MP at native frame +0x10..+0x16.
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(8, 2),
            (ushort)Math.Clamp(character.MaxHp, 1, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(10, 2),
            (ushort)Math.Clamp(character.MaxMp, 1, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(12, 2),
            (ushort)Math.Clamp(character.CurrentHp, 0, Math.Max(0, character.MaxHp)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(14, 2),
            (ushort)Math.Clamp(character.CurrentMp, 0, Math.Max(0, character.MaxMp)));
        return payload;
    }

    private async Task<byte[]?> HandleDungeonRevivalRetryAsync(
        byte[] frame,
        string channel,
        string remote,
        ConnectionSession session,
        byte[] payload,
        CancellationToken token)
    {
        if (!string.Equals(channel, "WorldAdapter", StringComparison.Ordinal)
            || !session.OnlineTracked
            || session.Character is null
            || payload.Length != 0
            || GetDungeonRoom(session) is null)
            return null;

        var nativeDeathLatched = false;
        if (session.NativeDungeon is not null && session.NativeCheckpoint is not null)
        {
            await CommitNativeCheckpointAsync(session, null, token);
            nativeDeathLatched = session.NativeCheckpoint?.Get(20) == 0;
        }

        DungeonBattleInstance? battle = null;
        var managedDeathClaimed = false;
        if (!nativeDeathLatched)
        {
            lock (_dungeonRoomGate)
            {
                var room = _dungeonRooms.GetValueOrDefault(session.DungeonRoomId);
                battle = room?.Battle;
                managedDeathClaimed = room is not null
                    && battle is not null
                    && battle.State == DungeonBattleState.Active
                    && room.Members.ContainsKey(session.SessionId)
                    && battle.DeadCharacters.Contains(session.Character.Id)
                    && battle.ContinuingCharacters.Add(session.Character.Id);
            }
        }

        var ack = BuildNativeFrame(frame, 0xCF96, [], session);
        if (!nativeDeathLatched && !managedDeathClaimed)
        {
            _log($"{channel}:{remote} dungeon revival retry idempotent ack: room={session.DungeonRoomId} character={session.Character.Id}");
            return ack;
        }

        var result = await _database.ConsumeRevivalRetryAsync(
            session.AccountId,
            session.Character.Id,
            session.SessionId,
            token);
        if (!result.Success)
        {
            if (managedDeathClaimed && battle is not null)
            {
                lock (_dungeonRoomGate)
                    battle.ContinuingCharacters.Remove(session.Character.Id);
            }
            _log($"{channel}:{remote} dungeon revival retry rejected: room={session.DungeonRoomId} character={session.Character.Id} error={result.Error}");
            return ack;
        }

        if (managedDeathClaimed && battle is not null)
        {
            lock (_dungeonRoomGate)
            {
                battle.DeadCharacters.Remove(session.Character.Id);
                battle.ContinuingCharacters.Remove(session.Character.Id);
            }
        }
        session.Character.RevivalUseCount = result.RevivalUseCount;
        session.Character.CurrentHp = result.CurrentHp;
        session.Character.CurrentMp = result.CurrentMp;
        if (session.NativeDungeon is not null && session.NativeCheckpoint is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(session.NativeCheckpoint.Bytes.AsSpan(20, 4), checked((uint)result.CurrentHp));
            BinaryPrimitives.WriteUInt32LittleEndian(session.NativeCheckpoint.Bytes.AsSpan(28, 4), checked((uint)result.CurrentMp));
            BinaryPrimitives.WriteUInt32LittleEndian(session.NativeCheckpoint.Bytes.AsSpan(60, 4), result.RevivalUseCount);
            session.NativeCheckpoint = await session.NativeDungeon.ExchangeAsync(null, session.NativeCheckpoint, token);
        }

        var revive = BuildNativeFrame(
            frame,
            0xCF84,
            BuildRevivalApplyPayload(session.Character),
            session);
        var refresh = BuildNativeFrame(frame, 0xCF72, BuildDungeonActorRefreshPayload(session.Character), session);
        _log($"{channel}:{remote} dungeon revival retry completed: room={session.DungeonRoomId} character={session.Character.Id} hp={result.CurrentHp} uses={result.RevivalUseCount}");
        return CombineNativeFrames(ack, revive, refresh);
    }

    internal static byte[] BuildRevivalApplyPayload(CharacterRecord character)
        => BuildDungeonContinueApplyPayload(character, variant: 60);

    internal static byte[] BuildDungeonContinueApplyPayload(
        CharacterRecord character,
        ushort variant)
    {
        if (variant is not (20 or 60))
            throw new ArgumentOutOfRangeException(nameof(variant));
        var payload = new byte[DungeonContinueResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), variant);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), GetSceneEntityId(character));
        // CF84 case 53124 consumes frame WORD +0x0C/+0x0E through the
        // actor's clamped HP/MP setters (sub_6E3000/sub_6E3050). DWORD
        // +0x10/+0x14 are separate actor auxiliary fields; they are not the
        // 0x044C battle position and remain initialized to zero.
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(4, 2),
            checked((ushort)Math.Clamp(character.CurrentHp, 1, ushort.MaxValue)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(6, 2),
            checked((ushort)Math.Clamp(character.CurrentMp, 1, ushort.MaxValue)));
        return payload;
    }

    internal static byte[] BuildDungeonActorRefreshPayload(CharacterRecord character)
    {
        var payload = new byte[0x74 - 8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), GetSceneEntityId(character));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), checked((ushort)Math.Clamp(character.MaxHp, 1, ushort.MaxValue)));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), checked((ushort)Math.Clamp(character.MaxMp, 1, ushort.MaxValue)));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), checked((ushort)Math.Clamp(character.CurrentHp, 0, Math.Max(0, character.MaxHp))));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), checked((ushort)Math.Clamp(character.CurrentMp, 0, Math.Max(0, character.MaxMp))));
        var appearance = BuildStoredAppearance(character);
        ReadOnlySpan<int> sourceOffsets = [0, 4, 8, 12, 20];
        for (var index = 0; index < sourceOffsets.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(0x14 - 8 + index * 4, 4),
                BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(sourceOffsets[index], 4)));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x2C - 8, 4), BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(24, 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x30 - 8, 4), GetEquippedPetItemCode(character));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x34 - 8, 4), character.Gender == 1 ? 1u : 0u);
        foreach (var quickSlot in character.QuickSlots)
        {
            if (quickSlot.Slot <= 5)
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x38 - 8 + quickSlot.Slot * 4, 4), quickSlot.ItemCode);
        }
        var expandedQuickSlotsActive = SkillSlotExpansionTime.TryDecode(character.QuickSlotExpansionExpires, out var expiration)
            && expiration > DateTime.Now;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x56 - 8, 2), expandedQuickSlotsActive ? (ushort)1 : (ushort)0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x64 - 8, 2), 1);
        payload[0x66 - 8] = checked((byte)Math.Clamp(character.InitialAttackMode + 1, 1, 3));
        payload[0x67 - 8] = payload[0x66 - 8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x6C - 8, 4), 100);
        payload[0x72 - 8] = 1;
        return payload;
    }

    private static byte[] BuildDungeonGameEventPayload(
        CharacterRecord character,
        ushort eventCode,
        ushort reportedDamage,
        ushort damage)
    {
        // Retail D010 consumes frame+0x10 as the authoritative post-hit HP
        // snapshot and frame+0x12 as the damage delta used by remote/display
        // paths. sub_6E3000 is an absolute HP setter.
        var payload = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(0, 2),
            GetSceneEntityId(character));
        payload[2] = character.CurrentHp <= 0 ? (byte)200 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(8, 2),
            (ushort)Math.Clamp(character.CurrentHp, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10, 2), damage);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18, 2), reportedDamage);
        payload[21] = (byte)Math.Clamp(eventCode, byte.MinValue, byte.MaxValue);
        if (eventCode is 20 or 30)
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(26, 2), damage);
        return payload;
    }

    private static byte[] BuildGameRoomLeavePayload(
        CharacterRecord character,
        CharacterRecord? owner = null)
    {
        var payload = new byte[4];
        var uid = (ushort)Math.Clamp(character.Id, 1L, (long)ushort.MaxValue);
        var ownerUid = (ushort)Math.Clamp(owner?.Id ?? character.Id, 1L, (long)ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), uid);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), ownerUid);
        return payload;
    }

    private static byte[] BuildMakeDdakgiRoomPayload()
    {
        // ANS_MAKE_DDAKGI_ROOM is a packed 10-byte frame. Its only payload
        // field is a ushort result, where zero means success.
        return new byte[2];
    }

    private static byte[] BuildGameDisconnectionPayload() =>
        BuildGameDisconnectionPayload(200);

    private static byte[] BuildGameDisconnectionPayload(uint result)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, result);
        return payload;
    }

    private byte[] BuildCreateGameResponse(byte[] request, ConnectionSession session)
    {
        return BuildNativeFrame(request, 0xCF6D, BuildCreateGameResponsePayload(session), session);
    }

    private byte[] BuildCreateGameResponsePayload(ConnectionSession session)
    {
        if (session.Character is null)
            throw new InvalidOperationException("A character is required to create a dungeon room.");
        var room = GetDungeonRoom(session)
            ?? throw new InvalidOperationException("The created dungeon room is unavailable.");
        return DungeonProtocol.BuildCreateResponse(
            checked((ushort)room.Id),
            session.Character.Id,
            session.Character.Name,
            DecodeDungeonPassword(room),
            room.SlotStates);
    }

    private static byte[] BuildCreateGamePayload(CharacterRecord character)
    {
        var payload = new byte[8];
        payload[0] = 10; // SUCCESS_CREATE
        payload[1] = 0; // normal dungeon room mode
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(2, 2),
            (ushort)Math.Clamp(character.Id, 1L, (long)ushort.MaxValue));
        payload[4] = 2;
        payload[5] = 2;
        payload[6] = 2; // close all three non-local player slots
        return payload;
    }

    private static byte[] BuildGameRoomUserPayload(
        CharacterRecord character,
        CharacterRecord? owner = null) =>
        BuildGameRoomUserPayload(character, owner, 0, 0, 0, 0, 0, 0);

    private static byte[] BuildGameRoomUserPayload(
        CharacterRecord character,
        CharacterRecord? owner,
        byte roomSlot)
        => BuildGameRoomUserPayload(character, owner, roomSlot, 0, 0, 0, 0, 0);

    private static byte[] BuildGameRoomUserPayload(
        CharacterRecord character,
        CharacterRecord? owner,
        byte roomSlot,
        byte readyRoomRank)
        => BuildGameRoomUserPayload(character, owner, roomSlot, 0, 0, 0, 0, readyRoomRank);

    private static byte[] BuildGameRoomUserPayload(
        CharacterRecord character,
        CharacterRecord? owner,
        byte roomSlot,
        uint skill0,
        byte skill0Grade,
        uint skill1,
        byte skill1Grade,
        byte readyRoomRank = 0)
    {
        var levelStart = CharacterProgression.ExperienceRequiredForLevel(character.Level);
        var nextLevel = character.Level >= CharacterProgression.MaximumLevel
            ? levelStart + 1
            : CharacterProgression.ExperienceRequiredForLevel(character.Level + 1);
        return DungeonProtocol.BuildRoomMember(
            character,
            (ushort)Math.Clamp(owner?.Id ?? character.Id, 1L, (long)ushort.MaxValue),
            roomSlot,
            0,
            GetEquippedPetItemCode(character),
            levelStart,
            nextLevel,
            skill0,
            skill0Grade,
            skill1,
            skill1Grade,
            readyRoomRank);
    }

    internal static bool TryParseCardSynthesisRequest(
        ReadOnlySpan<byte> payload,
        out ushort unionType,
        out ushort padding,
        out uint recipeToken)
    {
        unionType = 0;
        padding = 0;
        recipeToken = 0;
        if (payload.Length != CardUnionRequestPayloadLength)
            return false;
        unionType = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        padding = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        recipeToken = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        return true;
    }

    internal static byte[] BuildCardSynthesisResultPayload(bool success, uint outputItemCode)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(0, 4),
            success ? CardUnionSuccessResult : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4, 4),
            success ? outputItemCode : 0u);
        return payload;
    }

    internal static byte[] BuildCardSynthesisFinishPayload()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 10u);
        return payload;
    }

    internal static byte[] BuildCardSummonPayload(CharacterRecord? character, DateTime? currentTime = null)
    {
        // C3EA is a fixed 12-byte payload. Both date fields use yyyyMMddHH.
        // The expiration comes from the persisted free-magic entitlement; the
        // current timestamp is always sent and is not gated by counted keys.
        var payload = new byte[12];
        payload[0] = character is null ? (byte)0 : Math.Min(character.CardGuideStep, (byte)3);
        payload[1] = character?.CardSummonCount ?? 0;
        payload[2] = character?.CardGoldenKeyCount ?? 0;
        payload[3] = character?.CardMysteryKeyCount ?? 0;
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4, 4),
            character?.FreeMagicExpansionExpires ?? 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(8, 4),
            SkillSlotExpansionTime.Encode(currentTime ?? DateTime.Now));
        return payload;
    }

    internal static bool TryApplyInventoryQuickSlotDelta(
        CharacterRecord character,
        ReadOnlySpan<byte> payload,
        out List<CharacterQuickSlotRecord> quickSlots)
    {
        quickSlots = character.QuickSlots
            .Select(slot => new CharacterQuickSlotRecord
            {
                Slot = slot.Slot,
                ItemCode = slot.ItemCode,
                InventoryIndex = slot.InventoryIndex
            })
            .ToList();
        if (payload.Length != InventoryChangeRequestPayloadLength)
            return false;

        var removedCount = payload[26];
        var addedCount = payload[27];
        if (removedCount > 6 || addedCount > 6)
            return false;

        var removedIdentities = new HashSet<byte>();
        for (var index = 0; index < removedCount; index++)
        {
            var identity = payload[28 + index];
            if (identity > 83 || !removedIdentities.Add(identity))
                return false;
        }
        quickSlots.RemoveAll(slot => removedIdentities.Contains(slot.InventoryIndex));

        for (var index = 0; index < addedCount; index++)
        {
            var recordOffset = 36 + index * 8;
            var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(recordOffset, 4));
            var identity = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(recordOffset + 4, 2));
            var selected = payload[recordOffset + 6];
            var destination = payload[recordOffset + 7];
            if (itemCode == 0 || identity > 83 || selected != 1 || destination > 5)
                return false;

            var inventoryIndex = checked((byte)identity);
            quickSlots.RemoveAll(slot => slot.InventoryIndex == inventoryIndex || slot.Slot == destination);
            quickSlots.Add(new CharacterQuickSlotRecord
            {
                Slot = destination,
                ItemCode = itemCode,
                InventoryIndex = inventoryIndex
            });
        }

        return quickSlots.Count <= 6
            && quickSlots.All(slot => slot.Slot <= 5 && slot.InventoryIndex <= 83 && slot.ItemCode != 0)
            && quickSlots.Select(slot => slot.Slot).Distinct().Count() == quickSlots.Count
            && quickSlots.Select(slot => slot.InventoryIndex).Distinct().Count() == quickSlots.Count;
    }

    private static byte[] BuildEmptyInventoryPayload()
        // status, mode, uint16 count
        => [1, 0, 0, 0];

    internal static byte[] BuildGameInventoryPayload(CharacterRecord? character)
    {
        var itemCodes = GetGameInventoryItemCodes(character);
        var expansionExpiration = character?.GameInventoryExpansionExpires ?? 0;
        if (itemCodes.Length == 0 && expansionExpiration == 0)
            return BuildEmptyInventoryPayload();

        // C430 has 84 fixed 8-byte records from frame+12 through frame+683.
        // Its optional equipped-item tail begins at frame+684; mode zero does
        // not consume the tail, but the fixed payload preserves the structure.
        var payload = new byte[680];
        payload[0] = 1;
        payload[1] = expansionExpiration != 0 ? (byte)6 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), (ushort)itemCodes.Length);
        for (var index = 0; index < itemCodes.Length; index++)
        {
            var record = payload.AsSpan(4 + index * 8, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0, 4), itemCodes[index]);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(4, 2), (ushort)index);
            // C430 row+6 is the selected/equipped bit, not quantity.  It is set
            // only when this exact inventory identity is currently referenced by
            // a quick slot; newly transferred shop items therefore start clear.
            record[6] = character?.QuickSlots.Any(slot =>
                slot.InventoryIndex == index && slot.ItemCode == itemCodes[index]) == true
                ? (byte)1
                : (byte)0;
            record[7] = 0;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(676, 4), expansionExpiration);
        return payload;
    }

    private static uint[] GetGameInventoryItemCodes(CharacterRecord? character)
        => character?.Items
            .Where(item => item.Quantity > 0
                && ShopCatalog.TryGet(item.ItemCode, out var catalogItem)
                && catalogItem.Section == InventorySection.GameItem
                && catalogItem.Category != 42)
            .OrderBy(item => item.ItemCode)
            .SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity))
            .Take(84)
            .ToArray() ?? [];

    internal static bool TryResolveGameInventoryIdentity(
        CharacterRecord? character,
        uint identity,
        out uint itemCode)
    {
        var itemCodes = GetGameInventoryItemCodes(character);
        if (identity >= itemCodes.Length)
        {
            itemCode = 0;
            return false;
        }
        itemCode = itemCodes[checked((int)identity)];
        return true;
    }

    internal static bool TryResolvePetChangeRequest(
        CharacterRecord character,
        ReadOnlySpan<byte> payload,
        out ushort operation,
        out uint petItemCode,
        out uint materialItemCode,
        out uint materialIdentity)
    {
        operation = payload.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2))
            : (ushort)0;
        petItemCode = 0;
        materialItemCode = 0;
        materialIdentity = 0;
        if (payload.Length != PetChangeRequestPayloadLength || operation is < 1 or > 4)
            return false;
        var pets = GetPetWireItemCodes(character);
        var petHandle = payload[2];
        materialIdentity = operation == 2 ? payload[5] : payload[4];
        if (petHandle >= pets.Length
            || !TryResolveGameInventoryIdentity(character, materialIdentity, out materialItemCode))
            return false;
        petItemCode = pets[petHandle];
        return true;
    }

    private static bool TryFindInventoryExpansionTicket(
        CharacterRecord character,
        byte expansionType,
        out ShopCatalogItem ticket,
        out byte inventorySlot)
    {
        var itemCodes = GetGameInventoryItemCodes(character);
        for (var index = 0; index < itemCodes.Length; index++)
        {
            if (!ShopCatalog.TryGet(itemCodes[index], out var candidate)
                || candidate.InventoryExpansionType != expansionType)
                continue;
            ticket = candidate;
            inventorySlot = checked((byte)index);
            return true;
        }
        ticket = null!;
        inventorySlot = 0;
        return false;
    }

    private static byte[] BuildAvatarInventoryPayload(CharacterRecord? character)
    {
        // The client consumes 12-byte entries: item, equipped flag, slot and
        // expiration. Appearance+4 is the base body resource, not clothing.
        ReadOnlySpan<int> appearanceOffsets = [0, 8, 12, 16, 20, 24];
        var items = new List<(uint ItemCode, ushort Equipped, ushort Slot)>(AvatarInventoryCapacity);
        if (character is not null)
        {
            var appearance = BuildStoredAppearance(character);
            for (ushort slot = 0; slot < appearanceOffsets.Length; slot++)
            {
                var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(
                    appearance.AsSpan(appearanceOffsets[slot], sizeof(uint)));
                if (itemCode != 0 && items.All(item => item.ItemCode != itemCode))
                    items.Add((itemCode, 1, slot));
            }
        }

        if (character is not null)
        {
            foreach (var ownedItem in character.Items)
            {
                if (items.Count >= AvatarInventoryCapacity)
                    break;
                if (ownedItem.Quantity == 0
                    || items.Any(item => item.ItemCode == ownedItem.ItemCode)
                    || !ShopCatalog.TryGet(ownedItem.ItemCode, out var catalogItem)
                    || catalogItem.Section != InventorySection.Clothing)
                    continue;
                items.Add((ownedItem.ItemCode, 0, checked((ushort)items.Count)));
            }
        }

        var expansionExpiration = character?.AvatarInventoryExpansionExpires ?? 0;
        var payload = new byte[expansionExpiration != 0
            ? 680
            : 4 + items.Count * AvatarInventoryRecordLength];
        payload[0] = 1; // status
        payload[1] = expansionExpiration != 0 ? (byte)4 : (byte)0; // mode
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), (ushort)items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var record = payload.AsSpan(
                4 + index * AvatarInventoryRecordLength,
                AvatarInventoryRecordLength);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0, 4), items[index].ItemCode);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(4, 2), items[index].Equipped);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(6, 2), items[index].Slot);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(8, 4), PermanentItemExpiration);
        }
        if (expansionExpiration != 0)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(676, 4), expansionExpiration);
        return payload;
    }

    private static string FormatAvatarInventorySummary(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            return $"invalidPayloadLength={payload.Length}";
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        var items = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var offset = 4 + index * AvatarInventoryRecordLength;
            if (offset > payload.Length - AvatarInventoryRecordLength)
                return $"count={count} truncatedAt={index}";
            var record = payload.Slice(offset, AvatarInventoryRecordLength);
            items.Add($"slot={BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(6, 2))}:item={BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0, 4))}");
        }
        return $"count={count} items=[{string.Join(',', items)}]";
    }

    private static string FormatPetInventorySummary(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            return $"invalidPayloadLength={payload.Length}";
        var count = payload[2];
        var selectedSlot = payload[3];
        if (count == 0)
            return $"count=0 selectedSlot={selectedSlot}";
        if (payload.Length < 4 + PetInventoryRecordLength)
            return $"count={count} selectedSlot={selectedSlot} truncatedPayloadLength={payload.Length}";

        var record = payload.Slice(4, PetInventoryRecordLength);
        return $"count={count} selectedSlot={selectedSlot} item={BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0, 4))} slot={BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(8, 2))} modelStage={record[10]} savedStage={record[11]} active={record[12]} transition={record[13]} growth={BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(16, 4))}";
    }

    private static string FormatCharacterRestoreSummary(CharacterRecord? character)
        => character is null
            ? "character=none"
            : $"character={character.Name} id={character.Id} level={character.Level} exp={character.Experience} tutorialCompleted={character.TutorialCompleted} map={character.CurrentMapId}/{character.CurrentTownPage} position=({character.PositionX},{character.PositionY}) hp={character.CurrentHp}/{character.MaxHp} mp={character.CurrentMp}/{character.MaxMp} petVariant={character.PetVariant} equippedPet={GetEquippedPetItemCode(character)} petLevel={character.PetLevel} petExp={character.PetExperience} appearance={FormatAppearanceHex(character.Appearance)}";

    private static string FormatAppearanceHex(byte[]? appearance)
        => appearance is { Length: > 0 }
            ? Convert.ToHexString(appearance)
            : "none";

    private static byte[] BuildEmptyInteriorInventoryPayload(byte requestMode)
        => BuildInteriorInventoryPayload(requestMode, null);

    private static byte[] BuildInteriorInventoryPayload(byte requestMode, CharacterRecord? character)
    {
        // The C40A consumer clears and repopulates its material controls for
        // modes 10 and 20. Only mode 30 skips the record loop.
        var itemCodes = requestMode == 30 || character is null
            ? []
            : GetInteriorItemCodes(character);
        var expansionExpiration = character?.InteriorInventoryExpansionExpires ?? 0;
        var payload = new byte[expansionExpiration != 0
            ? 2024
            : 4 + itemCodes.Length * InteriorInventoryRecordLength];
        payload[0] = requestMode;
        payload[1] = expansionExpiration != 0 ? (byte)4 : (byte)1;
        payload[2] = 0;
        payload[3] = checked((byte)itemCodes.Length);
        for (var index = 0; index < itemCodes.Length; index++)
        {
            var record = payload.AsSpan(4 + index * InteriorInventoryRecordLength, InteriorInventoryRecordLength);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0, 4), itemCodes[index]);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(4, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(6, 2), checked((ushort)index));
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(8, 4), PermanentItemExpiration);
        }
        if (expansionExpiration != 0)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2020, 4), expansionExpiration);
        return payload;
    }

    private static uint[] GetInteriorItemCodes(CharacterRecord character)
        => character.Items
            .Where(item => item.Quantity > 0
                && ShopCatalog.TryGet(item.ItemCode, out var catalogItem)
                && catalogItem.Section == InventorySection.Furniture)
            .SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity))
            .Take(InteriorInventoryCapacity)
            .ToArray();

    private static byte[] BuildEmptyCashInventoryPayload(byte requestMode)
        // The C474 consumer ignores frame+8/+9, then reads mode and byte count.
        => [0, 0, requestMode, 0];

    // sub_813BC0 displays the whole C474 list, but hit testing has only 14 slots.
    internal const int CashInventoryPageSize = 14;

    internal static byte[] BuildCashInventoryPayload(byte requestMode, ushort page, CharacterRecord? character)
    {
        if (requestMode != 1 || page is 0 or > byte.MaxValue || character is null)
            return BuildEmptyCashInventoryPayload(requestMode);

        // C473 pages are one-based. The client replaces this list on every reply;
        // it does not slice C474 locally. Sort before expanding duplicate instances.
        var itemCodes = character.CashInboxItems
            .Where(item => item.Quantity > 0
                && ShopCatalog.TryGet(item.ItemCode, out _))
            .OrderBy(item => item.ItemCode)
            .SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity))
            .Skip((page - 1) * CashInventoryPageSize)
            .Take(CashInventoryPageSize)
            .ToArray();
        if (itemCodes.Length == 0)
            return BuildEmptyCashInventoryPayload(requestMode);

        // C474 mode 1 consumes byte count at frame+11 and 8-byte records
        // from frame+12: uint item code followed by uint expiration.
        var payload = new byte[4 + itemCodes.Length * 8];
        payload[2] = requestMode;
        payload[3] = checked((byte)itemCodes.Length);
        for (var index = 0; index < itemCodes.Length; index++)
        {
            var record = payload.AsSpan(4 + index * 8, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(record[..4], itemCodes[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], PermanentItemExpiration);
        }
        return payload;
    }

    private static byte[] BuildTokenInventoryPayload(CharacterRecord? character)
    {
        var itemCodes = character?.Items
            .Where(item => item.Quantity > 0
                && ShopCatalog.TryGet(item.ItemCode, out var catalogItem)
                && catalogItem.Category == 42)
            .SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity))
            .Take(ushort.MaxValue)
            .ToArray() ?? [];

        // C46A carries the category-42 mike inventory. Category-47 card keys
        // are C430 game-inventory rows and use that row's identity in C46D.
        var payload = new byte[4 + itemCodes.Length * 8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), checked((ushort)itemCodes.Length));
        for (var index = 0; index < itemCodes.Length; index++)
        {
            var record = payload.AsSpan(4 + index * 8, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(record[..4], itemCodes[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], checked((uint)index));
        }
        return payload;
    }

    private static void CaptureMikeItemSelectors(ConnectionSession session, ReadOnlySpan<byte> tokenInventoryPayload)
    {
        session.MikeItemSelectors.Clear();
        if (tokenInventoryPayload.Length < 4)
            return;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(tokenInventoryPayload.Slice(2, 2));
        for (var index = 0; index < count; index++)
        {
            var offset = 4 + index * 8;
            if (offset + 8 > tokenInventoryPayload.Length)
                break;
            var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(tokenInventoryPayload.Slice(offset, 4));
            if (!ShopCatalog.TryGet(itemCode, out var catalogItem) || catalogItem.Category != 42)
                continue;
            var selector = BinaryPrimitives.ReadUInt32LittleEndian(tokenInventoryPayload.Slice(offset + 4, 4));
            session.MikeItemSelectors[selector] = itemCode;
        }
    }

    internal static byte[] BuildPetInventoryPayload(CharacterRecord? character)
    {
        var petItems = GetPetWireItemCodes(character);
        var equippedPetItemCode = GetEquippedPetItemCode(character);
        if (equippedPetItemCode != 0 && !petItems.Contains(equippedPetItemCode))
            equippedPetItemCode = 0;
        var hasPet = petItems.Length > 0;
        var expansionExpiration = character?.PetInventoryExpansionExpires ?? 0;
        // In mode 4 the client restores its carried-pet global from
        // frame+2028. Any other mode explicitly clears that global.
        var payload = new byte[hasPet || expansionExpiration != 0 ? 2024 : 4];
        payload[0] = 1; // status
        payload[1] = expansionExpiration != 0
            ? (byte)6
            : equippedPetItemCode != 0 ? (byte)4 : (byte)0;
        payload[2] = (byte)petItems.Length;
        payload[3] = equippedPetItemCode == 0 ? (byte)0 : checked((byte)Array.IndexOf(petItems, equippedPetItemCode));
        if (!hasPet || character is null)
        {
            if (expansionExpiration != 0)
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1016, 4), expansionExpiration);
            return payload;
        }

        for (var index = 0; index < petItems.Length; index++)
        {
            var record = payload.AsSpan(4 + index * PetInventoryRecordLength, PetInventoryRecordLength);
            var state = PetProgression.GetState(character, petItems[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0, 4), petItems[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(4, 4), PermanentItemExpiration);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(8, 2), checked((ushort)index));
            record[10] = state.CurrentStage;
            record[11] = state.MaximumStage;
            record[12] = 1;
            record[13] = 0;
            BinaryPrimitives.WriteInt16LittleEndian(record.Slice(14, 2), state.Durability);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(16, 4), state.Experience);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(20, 4), state.Accessory0);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(24, 4), state.Accessory1);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(28, 4), state.Accessory2);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(32, 4), state.Level);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1016, 4), expansionExpiration);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2020, 4), equippedPetItemCode);
        return payload;
    }

    internal static uint[] GetPetWireItemCodes(CharacterRecord? character)
    {
        var owned = GetOwnedPetItemCodes(character).Distinct().ToArray();
        if (owned.Length <= PetChargeCapacity)
            return owned;
        var visible = owned.Take(PetChargeCapacity).ToArray();
        var equipped = GetEquippedPetItemCode(character);
        if (equipped != 0 && !visible.Contains(equipped))
            visible[^1] = equipped;
        return visible;
    }

    private static IEnumerable<uint> GetOwnedPetItemCodes(CharacterRecord? character)
    {
        if (character is null)
            yield break;
        if (character.PetVariant is >= 1 and <= 3)
            yield return 15_000_000u + (uint)character.PetVariant;
        foreach (var item in character.Items)
        {
            if (item.Quantity > 0 && item.ItemCode / 1_000_000 == 15)
                yield return item.ItemCode;
        }
    }

    private static uint GetEquippedPetItemCode(CharacterRecord? character)
    {
        if (character is null)
            return 0;
        var owned = GetOwnedPetItemCodes(character).Distinct().ToArray();
        if (character.EquippedPetItemCode != 0 && owned.Contains(character.EquippedPetItemCode))
            return character.EquippedPetItemCode;
        return character.PetVariant is >= 1 and <= 3
            ? 15_000_000u + (uint)character.PetVariant
            : 0;
    }

    internal static byte[] BuildTokenUseResultPayload(bool success, uint itemCode, uint identity)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), success ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), itemCode);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), identity);
        return payload;
    }

    private static byte[] BuildPetChangeResultPayload(byte operation, byte state, bool success)
    {
        var payload = new byte[4];
        // C450 accepts 2000 and dispatches the client's operations 1/2.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), success ? (ushort)2000 : (ushort)0);
        payload[2] = operation;
        payload[3] = state;
        return payload;
    }

    private static byte[] BuildInventoryChangeResultPayload(bool success)
    {
        // C47E reads a uint16 result at frame+8. The client requires 2000,
        // then commits its local appearance and releases the inventory dialog.
        // Its fixed response object extends through frame+47.
        var payload = new byte[InventoryChangeResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), success ? (ushort)2000 : (ushort)0);
        return payload;
    }

    private static byte[] BuildSlottedInventoryDeleteResultPayload(
        bool success,
        uint itemCode,
        ushort slot,
        int payloadLength,
        int slotOffset)
    {
        var payload = new byte[payloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), success ? 200u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), itemCode);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(slotOffset, 2), slot);
        return payload;
    }

    private static byte[] BuildUserDataChangePayload(CharacterRecord character)
    {
        // C47F is a 52-byte frame. The client consumes the character UID,
        // pet growth state, then a complete 36-byte appearance snapshot.
        var payload = new byte[44];
        var equippedPetItemCode = GetEquippedPetItemCode(character);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(0, 2),
            (ushort)Math.Clamp(character.Id, 1L, (long)ushort.MaxValue));
        var petState = PetProgression.GetState(character, equippedPetItemCode);
        // C47F byte +2/+3 are the pet model and upgrade stages, matching the
        // reference adapter's BuildChangeUserDataResponse. Pet experience is
        // carried separately at +4; character metadata must not be reused.
        payload[2] = petState.CurrentStage;
        payload[3] = petState.MaximumStage;
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4, 4),
            petState.Experience);
        BuildStoredAppearance(character).CopyTo(payload, 8);
        return payload;
    }

    private static byte[] BuildPetDeleteResultPayload(bool success, uint itemCode, ushort slot)
    {
        // C44E compares the dword at frame+8 with 200. On success it passes
        // frame+12 and frame+20 to the pet-inventory update routine.
        var payload = new byte[PetDeleteResponsePayloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), success ? 200u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), itemCode);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12, 2), slot);
        return payload;
    }

    private static byte[] BuildTaskActivationResultPayload(
        bool success,
        byte taskType,
        byte runtimeState,
        uint questId)
    {
        // C596 reads result at frame+8, the task type at +10, and quest ID at +12.
        var payload = new byte[TaskMutationResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), success ? (ushort)0 : (ushort)1);
        payload[2] = taskType;
        payload[3] = runtimeState;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), questId);
        return payload;
    }

    private static byte[] BuildTaskAbandonResultPayload(bool success, uint questId)
    {
        // C598 reads result at frame+8 and removes the runtime quest at frame+12.
        var payload = new byte[TaskMutationResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), success ? (ushort)0 : (ushort)1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), questId);
        return payload;
    }

    private static byte[] BuildTaskCompletionResultPayload(
        bool success,
        uint questId,
        CharacterRecord character,
        bool hansChanged,
        int gainedLevels)
    {
        // C59A consumes result/changed flags at frame+8..+11, quest ID at +12,
        // the level-up flag at +16, then level/HP/MP/experience at +19..+35.
        var payload = new byte[TaskCompletionResponsePayloadLength];
        payload[0] = success ? (byte)0 : (byte)3;
        if (!success)
            return payload;

        payload[1] = hansChanged ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), questId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), gainedLevels > 0 ? (ushort)1 : (ushort)0);
        payload[11] = (byte)Math.Clamp(character.Level, 1, byte.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(12, 2),
            (ushort)Math.Clamp(character.MaxHp, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(14, 2),
            (ushort)Math.Clamp(character.MaxMp, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(16, 4),
            (uint)Math.Clamp(character.Experience, 0L, uint.MaxValue));

        var levelStart = CharacterProgression.ExperienceRequiredForLevel(character.Level);
        var nextLevel = character.Level >= CharacterProgression.MaximumLevel
            ? levelStart + 1
            : CharacterProgression.ExperienceRequiredForLevel(character.Level + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(20, 4),
            (uint)Math.Clamp(levelStart, 0L, uint.MaxValue - 1L));
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(24, 4),
            (uint)Math.Clamp(nextLevel, levelStart + 1, uint.MaxValue));
        return payload;
    }

    private static byte[] BuildQuestScrollPurchaseResultPayload(
        QuestScrollPurchaseStatus status,
        long hans)
    {
        // C59F switches on the dword at frame+8 and, on success, passes the
        // two dwords at frame+16/+20 to the client's 64-bit Hans setter.
        var payload = new byte[QuestScrollPurchaseResponsePayloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)status);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8, 8), (ulong)Math.Max(0L, hans));
        return payload;
    }

    private static byte[] BuildTaskListPayload(IReadOnlyList<CharacterTaskRecord> tasks)
    {
        // C59C reads a dword count at frame+8, up to ten 20-byte entries at
        // frame+12, and fixed slot-type 2/1 records at frame+212/+232.
        var payload = new byte[TaskListResponsePayloadLength];
        var normalTasks = tasks
            .Where(task => task.SlotType == 0)
            .Take(NormalTaskCapacity)
            .ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)normalTasks.Length);
        for (var index = 0; index < normalTasks.Length; index++)
            WriteTaskRecord(payload.AsSpan(4 + index * TaskRecordLength, TaskRecordLength), normalTasks[index]);

        var fixedType2 = tasks.FirstOrDefault(task => task.SlotType == 2);
        if (fixedType2 is not null)
            WriteTaskRecord(payload.AsSpan(204, TaskRecordLength), fixedType2);
        var fixedType1 = tasks.FirstOrDefault(task => task.SlotType == 1);
        if (fixedType1 is not null)
            WriteTaskRecord(payload.AsSpan(224, TaskRecordLength), fixedType1);
        return payload;
    }

    private static void WriteTaskRecord(Span<byte> destination, CharacterTaskRecord task)
    {
        destination.Clear();
        destination[0] = 1;
        destination[1] = task.RuntimeState;
        destination[2] = task.TaskType;
        destination[3] = task.State3;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2), task.Progress1);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2), task.Progress2);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), task.Progress3);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), task.QuestId);
    }

    private static byte[] BuildAuctionListPayload(
        uint resultCode,
        uint totalPages,
        IReadOnlyList<AuctionListingRecord> listings,
        long viewerCharacterId)
    {
        var payload = new byte[AuctionListResponsePayloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), resultCode);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), totalPages);
        for (var index = 0; index < Math.Min(listings.Count, AuctionListEntryCapacity); index++)
        {
            var listing = listings[index];
            var record = payload.AsSpan(8 + index * AuctionListEntryLength, AuctionListEntryLength);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(0, 8), listing.UniqueNumber);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(8, 4), listing.ItemCode);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(12, 4), listing.HansPerItem);
            record[16] = listing.OriginalQuantity;
            record[17] = listing.RemainingQuantity;
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.Slice(18, 2),
                listing.SellerCharacterId == viewerCharacterId ? (ushort)1 : (ushort)0);
            // The retail client copies record+20 but never reads it. Keep the
            // official reserved dword zero instead of assigning invented state.
        }
        return payload;
    }

    private static byte[] BuildAuctionRegistrationResultPayload(uint resultCode, uint uniqueNumber)
    {
        var payload = new byte[AuctionRegisterResponsePayloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), resultCode);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), uniqueNumber);
        return payload;
    }

    private static byte[] BuildAuctionResultPayload(uint resultCode)
    {
        var payload = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, resultCode);
        return payload;
    }

    internal static byte[] BuildBoxInfoPayload(CharacterRecord? character)
        => BuildBoxInfoPayloadWithSkills(character, null);

    internal static byte[] BuildBoxInfoPayloadWithSkills(
        CharacterRecord? character,
        IReadOnlyList<CharacterSkillRecord>? learnedSkills,
        DateTime? currentTime = null)
    {
        // C379 builds the equipped clothing slots from frame+11's count and
        // the 12-byte records at frame+12. It does not derive those slots
        // from the separate appearance copy at frame+132.
        var payload = new byte[BoxInfoResponsePayloadLength];
        if (character is null)
            return payload;

        payload[1] = 1; // frame+9: box data loaded
        ReadOnlySpan<int> appearanceOffsets = [0, 8, 12, 16, 20, 24];
        var appearance = BuildStoredAppearance(character);
        var itemCount = 0;
        foreach (var appearanceOffset in appearanceOffsets)
        {
            if (appearanceOffset > appearance.Length - sizeof(uint))
                continue;
            var itemCode = BinaryPrimitives.ReadUInt32LittleEndian(
                appearance.AsSpan(appearanceOffset, sizeof(uint)));
            if (itemCode == 0)
                continue;

            var record = payload.AsSpan(4 + itemCount * 12, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0, 4), itemCode);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(4, 4), (uint)itemCount);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(8, 4), PermanentItemExpiration);
            itemCount++;
        }
        payload[3] = (byte)itemCount; // frame+11

        BuildStoredAppearance(character).CopyTo(payload, 124);

        var equippedPetItemCode = GetEquippedPetItemCode(character);
        if (equippedPetItemCode != 0)
        {
            // C379's equipped-pet record at frame+168 contains the same pet
            // state consumed from a C44C record, with expiration at +196.
            var pet = payload.AsSpan(160, 36);
            var state = PetProgression.GetState(character, equippedPetItemCode);
            BinaryPrimitives.WriteUInt32LittleEndian(
                pet.Slice(0, 4),
                equippedPetItemCode);
            var selectedPetHandle = Array.IndexOf(GetPetWireItemCodes(character), equippedPetItemCode);
            BinaryPrimitives.WriteUInt16LittleEndian(
                pet.Slice(4, 2),
                selectedPetHandle >= 0 ? checked((ushort)selectedPetHandle) : ushort.MaxValue);
            pet[6] = state.CurrentStage;
            pet[7] = state.MaximumStage;
            pet[8] = 1;
            pet[9] = 0;
            BinaryPrimitives.WriteInt16LittleEndian(pet.Slice(10, 2), state.Durability);
            BinaryPrimitives.WriteUInt32LittleEndian(pet.Slice(12, 4), state.Experience);
            BinaryPrimitives.WriteUInt32LittleEndian(pet.Slice(16, 4), state.Accessory0);
            BinaryPrimitives.WriteUInt32LittleEndian(pet.Slice(20, 4), state.Accessory1);
            BinaryPrimitives.WriteUInt32LittleEndian(pet.Slice(24, 4), state.Accessory2);
            BinaryPrimitives.WriteUInt32LittleEndian(
                pet.Slice(28, 4),
                PermanentItemExpiration);
            BinaryPrimitives.WriteUInt32LittleEndian(pet.Slice(32, 4), state.Level);
        }

        // C379 restores the six game-item quick slots from full-frame +0xE4.
        // Each 12-byte row carries code, the same C430 identity, and a reserved dword.
        foreach (var quickSlot in character.QuickSlots)
        {
            if (quickSlot.Slot > 5 || quickSlot.ItemCode == 0)
                continue;
            var row = payload.AsSpan(220 + quickSlot.Slot * 12, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(0, 4), quickSlot.ItemCode);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(4, 4), quickSlot.InventoryIndex);
        }

        // C379 consumes the two uint64 balances at frame+208/frame+216.
        // Keep their order identical to C37B: Hans first, Cash second.
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(200, 8),
            (ulong)Math.Max(0L, character.Hans));
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(208, 8),
            (ulong)Math.Max(0L, character.Cash));
        // C379 restores the inventory-window Z/X skill cells independently
        // from C3E8 and the dungeon CF71/CF72 carriers. Full-frame +0x130/+0x132
        // are the selected grades (one byte in each two-byte cell), while
        // +0x134/+0x138 are the exact selected skill codes. Publish only learned
        // catalog skills so stale profile values cannot create phantom cells.
        var skillGrades = (learnedSkills ?? [])
            .Where(skill => skill.Grade is >= 1 and <= 5
                && SkillCatalog.TryGet(skill.SkillCode, out _))
            .GroupBy(skill => skill.SkillCode)
            .ToDictionary(group => group.Key, group => group.Max(skill => skill.Grade));
        var selectedSkill0 = skillGrades.ContainsKey(character.SelectedSkill0)
            ? character.SelectedSkill0
            : 0u;
        var selectedSkill1 = skillGrades.ContainsKey(character.SelectedSkill1)
            ? character.SelectedSkill1
            : 0u;
        payload[296] = skillGrades.GetValueOrDefault(selectedSkill0);
        payload[298] = skillGrades.GetValueOrDefault(selectedSkill1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(300, 4), selectedSkill0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(304, 4), selectedSkill1);

        // C379 compares its encoded current time at frame+320 with the quick
        // bar and skill-slot expirations at frame+300 and frame+316.
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(292, 4),
            character.QuickSlotExpansionExpires);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(308, 4),
            character.SkillSlotExpansionExpires);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(312, 4),
            SkillSlotExpansionTime.Encode(currentTime ?? DateTime.Now));
        return payload;
    }

    private static byte[] BuildDdakgiGuideStepResultPayload(bool success = true)
    {
        var payload = new byte[4];
        // The C3FC consumer requires frame+8 == 1 and ignores the trailing word.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), success ? (ushort)1 : (ushort)0);
        return payload;
    }

    private static byte[] BuildShopMovePayload(CharacterRecord? character)
    {
        // C37B consumes two uint64 values: Hans at frame+8 and Cash at +16.
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(0, 8),
            (ulong)Math.Max(0L, character?.Hans ?? 0L));
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(8, 8),
            (ulong)Math.Max(0L, character?.Cash ?? 0L));
        return payload;
    }

    private static byte[] BuildVillageShopEnterResultPayload(byte shopCode, bool success)
    {
        // C3AC checks the byte at frame+8 for 100, then reads the shop code
        // at frame+9 before switching the client to ROOM_STATE.
        return [success ? (byte)100 : (byte)0, shopCode];
    }

    private static byte[] BuildRoomEntityLeavePayload(CharacterRecord character)
    {
        // C391 and C3AF both remove the room entity keyed by the DWORD at
        // frame+8. The corresponding C390/C3AE entity ids are the same value.
        var payload = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload,
            checked((uint)Math.Clamp(character.Id, 1L, (long)ushort.MaxValue)));
        return payload;
    }

    private static byte[] BuildShopUserInfoPayload(CharacterRecord character)
    {
        // The C3AE consumer reads a fixed 108-byte frame: name at +8,
        // entity id at +24, a 36-byte appearance at +28 and coordinates at
        // +104/+106. Remaining packed flags are client-owned defaults.
        var payload = new byte[ShopUserInfoResponsePayloadLength];
        WriteFixedGbk(payload.AsSpan(0, 16), character.Name);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(16, 2),
            (ushort)Math.Clamp(character.Id, 1L, (long)ushort.MaxValue));
        payload[18] = (byte)Math.Clamp(character.Gender, 0, byte.MaxValue);
        payload[19] = (byte)Math.Clamp(character.Face, 0, byte.MaxValue);
        BuildStoredAppearance(character).CopyTo(payload, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(96, 2),
            (ushort)Math.Clamp(character.PositionX, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(98, 2),
            (ushort)Math.Clamp(character.PositionY, 0, ushort.MaxValue));
        return payload;
    }

    private static byte[] BuildPetChargeListPayload(CharacterRecord? character)
    {
        // C452 is fixed at 2040 bytes. Its consumer reads a DWORD count at
        // frame+8, up to 56 records of 36 bytes from frame+12, then DWORD
        // Hans/Cash balances at frame+2032/+2036.
        var payload = new byte[PetChargeListResponsePayloadLength];
        var petItems = GetPetWireItemCodes(character);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)petItems.Length));

        if (character is not null)
        {
            var equippedPetItemCode = GetEquippedPetItemCode(character);
            for (var index = 0; index < petItems.Length; index++)
            {
                var record = payload.AsSpan(4 + index * PetInventoryRecordLength, PetInventoryRecordLength);
                var state = PetProgression.GetState(character, petItems[index]);
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0, 4), petItems[index]);
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(4, 4), PermanentItemExpiration);
                BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(8, 2), checked((ushort)index));
                record[10] = state.CurrentStage;
                record[11] = state.MaximumStage;
                record[12] = petItems[index] == equippedPetItemCode ? (byte)1 : (byte)0;
                record[13] = 0;
                BinaryPrimitives.WriteInt16LittleEndian(record.Slice(14, 2), state.Durability);
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(16, 4), state.Experience);
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(20, 4), state.Accessory0);
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(24, 4), state.Accessory1);
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(28, 4), state.Accessory2);
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(32, 4), state.Level);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(2024, 4),
                (uint)Math.Clamp(character.Hans, 0L, uint.MaxValue));
            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(2028, 4),
                (uint)Math.Clamp(character.Cash, 0L, uint.MaxValue));
        }
        return payload;
    }

    private static byte[] BuildPetChargeResultPayload(bool success)
    {
        // C454 reads only the DWORD at frame+8. Result 2 is the success path
        // that refreshes C451; result 1 displays the rejected-charge dialog.
        var payload = new byte[PetChargeResponsePayloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, success ? 2u : 1u);
        return payload;
    }

    private static byte[] BuildShopPurchaseResultPayload(
        byte resultCode,
        byte paymentMode,
        uint itemCode,
        ushort newQuantity,
        byte inventoryUpdate,
        long cash,
        long hans)
    {
        // C432 is a 104-byte frame in this client build. The consumer reads
        // result/payment at frame+8/+9, then Cash at +88 and Hans at +96.
        var payload = new byte[ShopPurchaseResponsePayloadLength];
        payload[0] = resultCode;
        payload[1] = paymentMode;
        if (resultCode == 10 && inventoryUpdate != 0)
        {
            payload[2] = inventoryUpdate;
            payload[3] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), itemCode);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10, 2), newQuantity);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(80, 8), (ulong)Math.Max(0, cash));
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(88, 8), (ulong)Math.Max(0, hans));
        return payload;
    }

    private static byte[] BuildNanaPurchaseResultPayload(
        byte resultCode,
        byte paymentMode,
        long cash,
        long hans)
    {
        // C3CE reads result/payment at frame+8/+9, NaNa/Cash at frame+16
        // and Hans at frame+24. The client treats each balance as a 64-bit value.
        var payload = new byte[NanaPurchaseResponsePayloadLength];
        payload[0] = resultCode;
        payload[1] = paymentMode;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8, 8), (ulong)Math.Max(0, cash));
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16, 8), (ulong)Math.Max(0, hans));
        return payload;
    }

    private static byte[] BuildShopGiftResultPayload(
        ushort resultCode,
        ushort cashFailureSubtype,
        long hans,
        long cash)
    {
        // All four native C47B consumers read the result/subtype at frame
        // +8/+10, Hans at +16 and Cash at +24. The complete frame is 32 bytes.
        var payload = new byte[ShopGiftResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), resultCode);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), cashFailureSubtype);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8, 8), (ulong)Math.Max(0, hans));
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16, 8), (ulong)Math.Max(0, cash));
        return payload;
    }

    private static byte[] BuildSpecialCardPurchaseResultPayload(
        ushort resultCode,
        ushort cashFailureSubtype)
    {
        // C492 is a fixed 12-byte frame. sub_617370 reads the result at
        // frame+8 and, for result 50, the money subtype at frame+10.
        var payload = new byte[SpecialCardPurchaseResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), resultCode);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), cashFailureSubtype);
        return payload;
    }

    private static int? GetRequiredGiftRecipientGender(ShopCatalogItem item)
    {
        if (item.Category != 10)
            return null;

        // Avatar item codes 100xxxxx and 101xxxxx are the client's female
        // and male variants respectively; any other variant is unrestricted.
        var genderVariant = (item.ItemCode / 100_000u) % 10u;
        return genderVariant <= 1 ? checked((int)genderVariant) : null;
    }

    private static bool IsNativeShopPaymentMode(byte paymentMode)
        => paymentMode is 0 or 2 or 3 or 4;

    private static byte[] BuildInteriorPurchaseResultPayload(
        byte resultCode,
        byte paymentMode,
        IReadOnlyList<(uint ItemCode, ushort NewQuantity)> items,
        long cash,
        long hans)
    {
        // C40C is a fixed 1040-byte frame. Its consumer reads the result at
        // frame+8, 12-byte inventory records from +12, Cash at +1024 and Hans
        // at +1032. Only successful purchases carry inventory records.
        var payload = new byte[InteriorPurchaseResponsePayloadLength];
        payload[0] = resultCode;
        payload[1] = paymentMode;
        if (resultCode == 10 && items.Count > 0)
        {
            var count = Math.Min(items.Count, InteriorInventoryCapacity);
            payload[2] = 1;
            payload[3] = checked((byte)count);
            for (var index = 0; index < count; index++)
            {
                var offset = 4 + index * InteriorPurchaseRecordLength;
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), items[index].ItemCode);
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 6, 2), items[index].NewQuantity);
            }
        }
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1016, 8), (ulong)Math.Max(0, cash));
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1024, 8), (ulong)Math.Max(0, hans));
        return payload;
    }

    private static byte[] BuildApartmentRecommendCountPayload(uint count)
    {
        // C399 reads a DWORD at frame+8 and passes its low byte to the dialog.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, count);
        return payload;
    }

    private static byte[] BuildInteriorWishlistPayload(
        IReadOnlyList<(uint ItemCode, ushort Quantity)> items)
    {
        // C418 reads a ushort count at frame+8. Each 12-byte record supplies
        // ItemCode at frame+12+n*12 and Quantity at frame+18+n*12.
        var count = Math.Min(items.Count, InteriorWishlistCapacity);
        var payload = new byte[4 + count * InteriorWishlistRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), checked((ushort)count));
        for (var index = 0; index < count; index++)
        {
            var offset = 4 + index * InteriorWishlistRecordLength;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), items[index].ItemCode);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 6, 2), items[index].Quantity);
        }
        return payload;
    }

    private static byte[] BuildInteriorWishlistChoiceResultPayload(uint resultCode)
    {
        // C41A switches on the DWORD at frame+8: 10 capacity reached,
        // 20 saved, and 30 for duplicate/rejected requests.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, resultCode);
        return payload;
    }

    private static byte[] BuildInteriorWishlistDeleteResultPayload(bool success)
    {
        // C41C removes the local entry only when frame+8 is 200.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, success ? 200u : 100u);
        return payload;
    }

    private static byte[] BuildShopWishlistPayload(
        IReadOnlyList<(uint ItemCode, uint WishlistId)> items)
    {
        // C438 reads a ushort count at frame+8. Each 8-byte record supplies
        // ItemCode at frame+12+n*8 and the stable wishlist id at frame+16+n*8.
        var count = Math.Min(items.Count, ShopWishlistCapacity);
        var payload = new byte[4 + count * ShopWishlistRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), checked((ushort)count));
        for (var index = 0; index < count; index++)
        {
            var offset = 4 + index * ShopWishlistRecordLength;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), items[index].ItemCode);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 4, 4), items[index].WishlistId);
        }
        return payload;
    }

    private static byte[] BuildNanaWishlistPayload(IReadOnlyList<uint> itemCodes)
    {
        // C3D5 reads a DWORD count at frame+8 followed by up to eight
        // 12-byte avatar records at frame+12. Only the item code is needed
        // to resolve the client catalog; the remaining fields mirror an
        // unequipped avatar record and provide a stable list slot.
        var count = Math.Min(itemCodes.Count, NanaWishlistCapacity);
        var payload = new byte[4 + count * NanaWishlistRecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)count));
        for (var index = 0; index < count; index++)
        {
            var record = payload.AsSpan(4 + index * NanaWishlistRecordLength, NanaWishlistRecordLength);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0, 4), itemCodes[index]);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(6, 2), checked((ushort)index));
        }
        return payload;
    }

    private static byte[] BuildNanaWishlistChoiceResultPayload(uint resultCode)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, resultCode);
        return payload;
    }

    private static byte[] BuildNanaWishlistDeleteResultPayload(bool success)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, success ? 200u : 100u);
        return payload;
    }

    private static byte[] BuildShopOwnedStatePayload(IReadOnlyList<uint> itemCodes)
    {
        // C42E reads the count at frame+10 and DWORD item codes from frame+12.
        var count = Math.Min(itemCodes.Count, ShopOwnedStateCapacity);
        var payload = new byte[4 + count * sizeof(uint)];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), checked((ushort)count));
        for (var index = 0; index < count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4 + index * sizeof(uint), sizeof(uint)), itemCodes[index]);
        return payload;
    }

    private static byte[] BuildShopWishlistChoiceResultPayload(bool success)
    {
        // C43A reloads C437 only when the DWORD at frame+8 is 200.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, success ? 200u : 100u);
        return payload;
    }

    private static byte[] BuildShopWishlistDeleteResultPayload(bool success)
    {
        // C43C reloads C437 only when the DWORD at frame+8 is 200;
        // 100 opens the client's deletion-failure dialog.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, success ? 200u : 100u);
        return payload;
    }

    private static uint GetPetShopUnitPrice(uint itemCode)
        => ShopCatalog.TryGet(15, itemCode, out var item) ? item.HansPrice : 0;

    private static byte GetPetModelStage(uint itemCode)
        => ShopCatalog.TryGet(15, itemCode, out var item) ? item.PetModelStage : (byte)0;

    private static byte GetPetUpgradeStage(uint itemCode)
        => ShopCatalog.TryGet(15, itemCode, out var item) ? item.PetUpgradeStage : (byte)0;

    private static int GetPetShopCatalogCount()
        => ShopCatalog.Pets;

    private static int GetShopCatalogCount()
        => ShopCatalog.Count;

    private static uint GetShopUnitPrice(byte category, uint itemCode)
        => ShopCatalog.TryGet(category, itemCode, out var item) ? item.HansPrice : 0;

    internal static bool IsSupportedShopWishlistCategory(byte category)
        => category is 14 or 15 or 17 or 18 or 21 or 42 or 43 or 44 or 45 or 47 or 48;

    private static bool IsSupportedSpecialShopCategory(byte category)
        => category is 42 or 43 or 44 or 45 or 47 or 48;

    private static int NormalizeTutorialPetVariant(ReadOnlySpan<byte> encodedPetCode)
    {
        if (encodedPetCode.Length != sizeof(uint))
            return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(encodedPetCode) switch
        {
            0x00E4E1C1 => 1,
            0x00E4E1C2 => 2,
            0x00E4E1C3 => 3,
            1 => 1,
            2 => 2,
            3 => 3,
            _ => 0
        };
    }

    private static byte[] BuildTradeInvitationPayload(CharacterRecord inviter)
    {
        var payload = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), GetSceneEntityId(inviter));
        WriteFixedGbk(payload.AsSpan(4, 16), inviter.Name);
        return payload;
    }

    private static byte[] BuildPartyInvitationPayload(
        byte[] requestPayload,
        CharacterRecord inviter)
    {
        if (requestPayload.Length != PartyInvitationPayloadLength)
            throw new InvalidDataException("C4E0 party invitation payload must be 24 bytes.");

        var payload = requestPayload.ToArray();
        payload.AsSpan(0, 16).Clear();
        WriteFixedGbk(payload.AsSpan(0, 16), inviter.Name);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16, 2), GetSceneEntityId(inviter));
        return payload;
    }

    private static byte[] BuildPartyUnionPayload(
        CharacterRecord owner,
        byte[] ownerMetadata,
        IReadOnlyList<CharacterRecord> otherMembers,
        byte answerCode)
    {
        var memberCount = Math.Min(otherMembers.Count, PartyMaximumMembers - 1);
        var payload = new byte[PartyMemberRecordLength + memberCount * PartyMemberRecordLength];
        WriteFixedGbk(payload.AsSpan(0, 16), owner.Name);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16, 2), GetSceneEntityId(owner));
        payload[18] = answerCode;
        NormalizePartyMetadata(owner, ownerMetadata).CopyTo(payload, 19);
        payload[23] = checked((byte)memberCount);

        for (var index = 0; index < memberCount; index++)
        {
            var member = otherMembers[index];
            var offset = PartyMemberRecordLength + index * PartyMemberRecordLength;
            WriteFixedGbk(payload.AsSpan(offset, 16), member.Name);
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(offset + 16, 2),
                GetSceneEntityId(member));
            BuildDefaultPartyMetadata(member).CopyTo(payload, offset + 18);
        }
        return payload;
    }

    private static byte[] NormalizePartyMetadata(CharacterRecord character, byte[] metadata)
    {
        var normalized = metadata.Length == 4 ? metadata.ToArray() : new byte[4];
        normalized[2] = (byte)Math.Clamp(character.Level, 1, byte.MaxValue);
        return normalized;
    }

    private static byte[] BuildDefaultPartyMetadata(CharacterRecord? character)
        => [0, 0, (byte)Math.Clamp(character?.Level ?? 1, 1, byte.MaxValue), 0];

    private static byte[] BuildPartyLeavePayload(
        ushort partyState,
        byte reason,
        bool ownerChanged,
        ushort newOwnerUid,
        ushort exitingUid)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), partyState);
        payload[2] = reason;
        payload[3] = ownerChanged ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), newOwnerUid);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), exitingUid);
        return payload;
    }

    private static byte[] BuildPartyOwnerChangePayload(bool success, ushort newOwnerUid)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), success ? (ushort)1 : (ushort)0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), newOwnerUid);
        return payload;
    }

    private static byte[] BuildPartyMemberRemovePayload(bool success, byte partyState, ushort targetUid)
    {
        var payload = new byte[4];
        payload[0] = success ? (byte)1 : (byte)0;
        payload[1] = partyState;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), targetUid);
        return payload;
    }

    private static byte[] BuildTradeInvitationFailurePayload(uint targetEntityUid)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, targetEntityUid);
        return payload;
    }

    private static byte[] BuildTradeAgreementPayload(CharacterRecord invitee, ushort result)
    {
        var payload = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), GetSceneEntityId(invitee));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), result);
        WriteFixedGbk(payload.AsSpan(4, 16), invitee.Name);
        return payload;
    }

    private static byte[] BuildTradeRoomCreateResultPayload(bool success, int roomId)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), success ? (ushort)1 : (ushort)0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(2, 2),
            success ? checked((ushort)roomId) : (ushort)0);
        return payload;
    }

    private static byte[] BuildTradeRoomInvitationPayload(int roomId)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)roomId));
        return payload;
    }

    private static byte[] BuildTradeRoomEnterResultPayload(int roomId)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), checked((ushort)roomId));
        return payload;
    }

    private static byte[] BuildTradePeerInfoPayload(CharacterRecord peer)
    {
        var payload = new byte[TradePeerInfoPayloadLength];
        WriteFixedGbk(payload.AsSpan(0, 16), peer.Name);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16, 2), GetSceneEntityId(peer));
        payload[18] = (byte)Math.Clamp(peer.Level, 1, byte.MaxValue);
        payload[19] = (byte)GetDungeonLevelIcon(peer.Level);
        BuildStoredAppearance(peer).CopyTo(payload, 20);
        return payload;
    }

    private static byte[] BuildTradeOfferResultPayload(
        bool success,
        bool hansChanged,
        byte slotIndex,
        TradeCardOffer? card)
    {
        var payload = new byte[TradeOfferResultPayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(0, 2),
            success ? (ushort)1 : (ushort)0);
        payload[2] = hansChanged ? (byte)1 : (byte)0;
        payload[3] = slotIndex;
        if (card is { } value)
        {
            payload[4] = value.Chapter;
            payload[5] = value.Page;
            payload[6] = value.Index;
            payload[7] = value.Count;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), value.ItemCode);
        }
        return payload;
    }

    private static byte[] BuildTradeReadyPayload(TradeOffer offer)
    {
        var payload = new byte[TradeReadyPayloadLength];
        for (var index = 0; index < TradeCardSlotCount; index++)
        {
            if (offer.Cards[index] is not { } card)
                continue;
            var offset = index * TradeCardRecordLength;
            payload[offset] = card.Chapter;
            payload[offset + 1] = card.Page;
            payload[offset + 2] = card.Index;
            payload[offset + 3] = card.Count;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 4, 4), card.ItemCode);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(40, 8), offer.Hans);
        return payload;
    }

    private static byte[] BuildTradeSettlementResultPayload(uint resultCode)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, resultCode);
        return payload;
    }

    private static byte[] BuildTownEnterPayload(byte townId, byte responsePage, byte responseFlag)
    {
        // C366 is a fixed 12-byte frame. The handler consumes only these four
        // bytes and then creates the local entity from the saved 271A context.
        // responsePage/responseFlag are the canonical server result, not a raw
        // echo of C365's transient page/mode fields.
        return [200, townId, responsePage, responseFlag];
    }

    private static byte[] BuildProfileResponsePayload(CharacterRecord? character)
    {
        // C377 reads a fixed frame through frame+135. Its remote-profile path
        // consumes the name at +8, selectors at +39/+41, max HP at +42 and
        // max MP at +44. The values at +46/+56 are separate derived profile
        // statistics, not base attributes; keep them zero until their retail
        // adapter semantics are independently established.
        var payload = new byte[128];
        WriteFixedGbk(payload.AsSpan(0, 24), character?.Name ?? "角色");

        var level = Math.Clamp(character?.Level ?? 1, 1, 99);
        payload[31] = (byte)level;

        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(34, 2),
            (ushort)Math.Clamp(character?.MaxHp ?? 160, 1, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(36, 2),
            (ushort)Math.Clamp(character?.MaxMp ?? 100, 1, ushort.MaxValue));

        return payload;
    }

    private static byte[] BuildMiniRoomMovePayload(
        CharacterRecord? owner,
        bool success,
        IReadOnlyList<ApartmentPlacementRecord>? placements = null)
    {
        // ANS_MOVE_MINIROOM is a fixed 112-byte frame. The client switches to
        // ROOM_STATE only for result 10, then consumes every field through the
        // second wall descriptor at frame+111.
        var payload = new byte[MiniRoomMoveResponsePayloadLength];
        if (!success || owner is null)
        {
            // Result 30 is the client's ordinary move-room failure branch.
            payload[0] = 30;
            return payload;
        }

        payload[0] = 10; // successful private-room move
        payload[1] = 10; // normal character room type

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encodedName = Encoding.GetEncoding(936).GetBytes(owner.Name);
        var ownerNameLength = Math.Min(encodedName.Length, 15);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), (ushort)ownerNameLength);
        encodedName.AsSpan(0, ownerNameLength).CopyTo(payload.AsSpan(4, 16));

        // A private room contains one local host entity. The following three
        // entity parameters and the paired room-state parameter are valid at
        // zero in the original constructors; the local-player flag itself is
        // supplied as the fixed value 1 by the C38E handler.
        payload[22] = 1;

        // Apartment entry coordinates are transient room coordinates and must
        // not reuse or overwrite the character's persisted town position.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(28, 2), 320);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(30, 2), 240);

        // C38E applies the floor and wall before C392 requests the room's
        // ordinary furniture objects. Both descriptors use the same 12-byte
        // layout as C424 at frame+88 and frame+100.
        placements ??= [];
        WriteApartmentSurfaceDescriptor(
            payload.AsSpan(80, ApartmentInteriorObjectRecordLength),
            placements.LastOrDefault(item => item.InteriorType == 0));
        WriteApartmentSurfaceDescriptor(
            payload.AsSpan(92, ApartmentInteriorObjectRecordLength),
            placements.LastOrDefault(item => item.InteriorType == 1));
        return payload;
    }

    internal static byte[] BuildMiniRoomObjectInfoPayload(
        IReadOnlyList<ApartmentPlacementRecord> placements)
    {
        // C393 is one fixed 1020-byte wire frame: an 8-byte transport header
        // plus a 1012-byte payload. The payload carries WORD count at +0,
        // WORD final-info at +2, and up to 84 twelve-byte ordinary-object rows
        // from +4. Value 6000 asks the client to request a continuation; this
        // adapter returns the complete bounded snapshot and marks it with 2000.
        var objects = placements
            .Where(item => item.InteriorType is >= 2 and <= 4)
            .Take(ApartmentInteriorObjectCapacity)
            .ToArray();
        var payload = new byte[MiniRoomObjectInfoResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(0, 2),
            checked((ushort)objects.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(2, 2),
            MiniRoomObjectInfoFinalSnapshot);
        for (var index = 0; index < objects.Length; index++)
            WriteApartmentObjectRecord(
                payload.AsSpan(4 + index * ApartmentInteriorObjectRecordLength, ApartmentInteriorObjectRecordLength),
                objects[index]);
        return payload;
    }

    private static byte[] BuildMiniRoomUserInfoPayload(
        CharacterRecord character,
        ushort positionX,
        ushort positionY)
    {
        // C390 is a fixed 124-byte frame. Its consumer reads the identity at
        // frame+8, entity id at +24, appearance at +28 and coordinates at
        // +120/+122.
        var payload = new byte[MiniRoomUserInfoResponsePayloadLength];
        WriteFixedGbk(payload.AsSpan(0, 16), character.Name);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(16, 2),
            (ushort)Math.Clamp(character.Id, 1L, (long)ushort.MaxValue));
        payload[19] = 1;
        BuildStoredAppearance(character).CopyTo(payload, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(112, 2), positionX);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(114, 2), positionY);
        return payload;
    }

    private static byte[] BuildApartmentInteriorInfoPayload(
        IReadOnlyList<ApartmentPlacementRecord> placements)
    {
        // C424 is a fixed 1044-byte frame. Objects occupy 84 twelve-byte
        // records from frame+12; floor and wall descriptors are fixed at
        // frame+1020 and frame+1032.
        var payload = new byte[ApartmentInteriorInfoResponsePayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 1000);
        var objects = placements
            .Where(item => item.InteriorType is >= 2 and <= 4)
            .Take(ApartmentInteriorObjectCapacity)
            .ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), checked((ushort)objects.Length));
        for (var index = 0; index < objects.Length; index++)
            WriteApartmentObjectRecord(
                payload.AsSpan(4 + index * ApartmentInteriorObjectRecordLength, ApartmentInteriorObjectRecordLength),
                objects[index]);

        WriteApartmentSurfaceDescriptor(
            payload.AsSpan(1012, ApartmentInteriorObjectRecordLength),
            placements.LastOrDefault(item => item.InteriorType == 0));
        WriteApartmentSurfaceDescriptor(
            payload.AsSpan(1024, ApartmentInteriorObjectRecordLength),
            placements.LastOrDefault(item => item.InteriorType == 1));
        return payload;
    }

    private static void WriteApartmentObjectRecord(
        Span<byte> destination,
        ApartmentPlacementRecord placement)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0, 4), placement.ItemCode);
        BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(4, 2), placement.X);
        BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(6, 2), placement.Y);
        destination[8] = placement.Layer;
        destination[9] = placement.InteriorType;
        destination[10] = placement.Mirror;
        destination[11] = placement.SlotIndex;
    }

    private static void WriteApartmentSurfaceDescriptor(
        Span<byte> destination,
        ApartmentPlacementRecord? placement)
    {
        if (placement is null)
            return;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0, 4), placement.ItemCode);
        destination[9] = placement.InteriorType;
        destination[11] = placement.SlotIndex;
    }

    private static byte[] BuildInteriorCatalogPayload(
        ushort category,
        ushort page,
        CharacterRecord character,
        IReadOnlyList<ApartmentPlacementRecord> placements)
    {
        // C406 has eight uint item slots followed by current floor and wall
        // ids at frame+44/+48.
        var payload = new byte[InteriorCatalogResponsePayloadLength];
        payload[0] = checked((byte)Math.Min(category, byte.MaxValue));
        var inventory = GetInteriorItemCodes(character);
        var offset = Math.Min(inventory.Length, page * InteriorCatalogCapacity);
        var items = inventory.Skip(offset).Take(InteriorCatalogCapacity).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), checked((ushort)items.Length));
        for (var index = 0; index < items.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4 + index * sizeof(uint), sizeof(uint)), items[index]);

        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(36, 4),
            placements.LastOrDefault(item => item.InteriorType == 0)?.ItemCode ?? uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(40, 4),
            placements.LastOrDefault(item => item.InteriorType == 1)?.ItemCode ?? uint.MaxValue);
        return payload;
    }

    private static byte[] BuildInteriorChangeResultPayload(bool success)
    {
        var payload = new byte[4];
        // The C412 consumer treats 1000 as failure and any other result as a
        // successful commit.
        BinaryPrimitives.WriteUInt32LittleEndian(payload, success ? 2000u : 1000u);
        return payload;
    }

    private static void WriteFixedGbk(Span<byte> destination, string value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        destination.Clear();
        var encoded = Encoding.GetEncoding(936).GetBytes(value);
        encoded.AsSpan(0, Math.Min(encoded.Length, Math.Max(0, destination.Length - 1))).CopyTo(destination);
    }

    private static bool TryDecodeFixedGbkString(
        ReadOnlySpan<byte> slot,
        bool allowEmpty,
        out string value)
    {
        value = string.Empty;
        var terminator = slot.IndexOf((byte)0);
        if (terminator < 0)
            return false;
        if (terminator == 0)
            return allowEmpty;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var strictGbk = Encoding.GetEncoding(
                936,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            value = strictGbk.GetString(slot[..terminator]);
            return !value.Any(char.IsControl);
        }
        catch (DecoderFallbackException)
        {
            value = string.Empty;
            return false;
        }
    }

    private static byte[] BuildStoredAppearance(CharacterRecord character)
    {
        // The client's avatar structure labels the dword at +28 as Pet.
        // Inventory ownership comes from C44C, while entity creation reads
        // this field from the 271A/C368/C36A/CF71 appearance block.
        var equippedPetItemCode = GetEquippedPetItemCode(character);
        // Offset +32 is the client avatar gender selector (0=female, 1=male).
        // Clearing this field makes every inventory action look cross-gender.
        return DatabaseService.NormalizeAppearanceForGender(
            character.Appearance,
            character.Gender,
            equippedPetItemCode);
    }

    internal static byte[] BuildRoomEnterPayload(
        CharacterRecord character,
        byte roomIndex,
        ushort entryPositionX,
        ushort entryPositionY)
    {
        // The native C368 frame is 60 bytes. The first 48 bytes carry the
        // result, room index and appearance; the final 12 bytes initialize
        // the local entity's entry position and HP/MP values. C367 supplies
        // the destination page's valid entry point; persisted coordinates may
        // belong to the previous page and must not be echoed across a move.
        var payload = new byte[52];
        payload[0] = 100;
        payload[1] = roomIndex;
        // frame+11 == 1 tells the client to copy frame+12's appearance and
        // restore the local entity model. Zero skips that entire branch.
        payload[3] = 1;
        BuildStoredAppearance(character).CopyTo(payload, 4);

        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(40, 2),
            entryPositionX);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(42, 2),
            entryPositionY);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(44, 2),
            (ushort)Math.Clamp(character.MaxHp, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(46, 2),
            (ushort)Math.Clamp(character.MaxMp, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(48, 2),
            (ushort)Math.Clamp(character.CurrentHp, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(50, 2),
            (ushort)Math.Clamp(character.CurrentMp, 0, ushort.MaxValue));
        return payload;
    }

    private static byte[] BuildTownUserInfoPayload(CharacterRecord character)
        => BuildTownUserInfoPayload(
            character,
            GetSceneEntityId(character),
            GetCharacterUid(character),
            (ushort)Math.Clamp(character.PositionX, 0, 0x3FF),
            (ushort)Math.Clamp(character.PositionY, 0, 0x3FF));

    private static byte[] BuildTownUserInfoPayload(
        CharacterRecord character,
        ushort positionX,
        ushort positionY)
        => BuildTownUserInfoPayload(
            character,
            GetSceneEntityId(character),
            GetCharacterUid(character),
            positionX,
            positionY);

    private static byte[] BuildTownUserInfoPayload(
        CharacterRecord character,
        ushort sceneEntityId,
        ushort characterUid,
        ushort positionX,
        ushort positionY)
    {
        // C36A is a fixed 112-byte frame. Its frame+60 control word carries:
        // bits 3..5 = 2: enqueue into MEDIATE_WAIT_MOVE_PACKET, then construct
        //                    a normal CVillageChar through sub_53E0C0;
        // bits 6..12:        7-bit character level;
        // bits 20..31:       12-bit scene UID.
        // A zero queue mode makes sub_53DDA0 construct a gaming character and
        // unconditionally attach the trade icon. The client only ships level
        // resources 1244..1282 for levels 1..39; higher values resolve to
        // unrelated interface art. CB21/CB22/C36B use the same scene UID.
        var payload = new byte[104];
        WriteFixedGbk(payload.AsSpan(0, 16), character.Name);
        BuildStoredAppearance(character).CopyTo(payload, 16);

        var characterLevel = (uint)Math.Clamp(character.Level, 1, 39);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(52, 4),
            (2u << 3) | (characterLevel << 6) | ((uint)sceneEntityId << 20));

        var packedPosition = ((uint)positionX << 2) | ((uint)positionY << 12);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(80, 4), packedPosition);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(102, 2),
            characterUid);
        return payload;
    }

    private static byte[] BuildTownMapMarkerPayload(CharacterRecord character)
    {
        // C36D is the client's CHouse/map-marker snapshot. The original
        // handler selects qz_village_icon_sex.im3 from frame+83:
        // 1 -> male frame 0, 0 -> female frame 1. Without this packet the
        // CHouse gender member remains uninitialized and visibly alternates
        // between male and female across launches.
        var payload = new byte[TownMapMarkerPayloadLength];
        WriteFixedGbk(payload.AsSpan(0, 16), character.Name);

        // frame+24/+28 are the exterior and banner codes. Zero asks the
        // client to keep its built-in default house exterior and no banner.
        // frame+32..+77 is the optional banner text and stays NUL-filled.
        // frame+81 is a CHouse slot inside the current map, not the town-page
        // number. The client creates these slots from zero upward and silently
        // ignores a snapshot whose slot was not created by the map resource.
        payload[73] = 0;
        payload[75] = (byte)Math.Clamp(character.Gender, 0, 1); // frame+83
        payload[76] = 100; // frame+84: this marker belongs to the local user
        payload[77] = 0;   // frame+85: unlocked/default interaction state
        return payload;
    }

    private static ushort GetSceneEntityId(CharacterRecord character)
        => WireIdentityAllocator.GetSceneEntityId(character.Id);

    private static ushort GetCharacterUid(CharacterRecord character)
        => WireIdentityAllocator.GetCharacterUid(character.Id);

    private static byte[] BuildSceneMovementPayload(byte[] payload, CharacterRecord character)
    {
        if (payload.Length != 16)
            throw new InvalidDataException("CB21 movement payload must be 16 bytes.");
        var result = payload.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14, 2), GetSceneEntityId(character));
        return result;
    }

    private static byte[] BuildTownLeavePayload(CharacterRecord character)
        => BuildTownLeavePayload(GetSceneEntityId(character));

    private static byte[] BuildTownLeavePayload(ushort sceneEntityId)
    {
        var payload = new byte[TownLeavePayloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, sceneEntityId);
        return payload;
    }

    private static bool TryParseNativeLoginCredentials(
        byte[] payload,
        out string username,
        out string password)
    {
        username = string.Empty;
        password = string.Empty;
        if (payload.Length != NativeLoginPayloadLength)
            return false;

        // REQ_TOPPIG_LOGIN (0x2713) stores two independent C strings at
        // payload +0 and +16. The final eight bytes belong to the original
        // client context and are deliberately ignored.
        var usernameBytes = payload.AsSpan(0, NativeLoginUsernameLength);
        var passwordBytes = payload.AsSpan(NativeLoginUsernameLength, NativeLoginPasswordLength);
        var usernameTerminator = usernameBytes.IndexOf((byte)0);
        var passwordTerminator = passwordBytes.IndexOf((byte)0);
        if (usernameTerminator >= 0)
            usernameBytes = usernameBytes[..usernameTerminator];
        if (passwordTerminator >= 0)
            passwordBytes = passwordBytes[..passwordTerminator];

        if (usernameBytes.Length is < 6 or > 12
            || passwordBytes.Length is < 6 or > NativeLoginPasswordLength)
            return false;
        foreach (var value in usernameBytes)
            if (value is < (byte)'0' or > (byte)'9')
                return false;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var clientEncoding = Encoding.GetEncoding(
                936,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            username = Encoding.ASCII.GetString(usernameBytes);
            password = clientEncoding.GetString(passwordBytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return password.Length is >= 6 and <= 16
            && password.All(character => !char.IsControl(character));
    }

    private static void ClearAuthenticatedSession(ConnectionSession session)
    {
        session.AccountId = 0;
        session.Username = string.Empty;
        session.RemoteIp = null;
        session.Character = null;
    }

    private static (string Name, int Gender, int Face, byte[] Appearance) ParseCharacterCreation(byte[] payload)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const int nameFieldLength = 16;
        const int appearanceLength = 36;

        var encodedName = payload.AsSpan(0, Math.Min(nameFieldLength, payload.Length));
        var terminator = encodedName.IndexOf((byte)0);
        if (terminator >= 0)
            encodedName = encodedName[..terminator];
        var name = Encoding.GetEncoding(936).GetString(encodedName).Trim();

        var appearance = new byte[appearanceLength];
        if (payload.Length > nameFieldLength)
            payload.AsSpan(nameFieldLength, Math.Min(appearanceLength, payload.Length - nameFieldLength)).CopyTo(appearance);

        var face = BinaryPrimitives.ReadInt32LittleEndian(appearance.AsSpan(0, 4));
        var gender = BinaryPrimitives.ReadUInt16LittleEndian(appearance.AsSpan(32, 2));
        return (name, gender, face, appearance);
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static string FormatNativeFrameHexForLog(ReadOnlySpan<byte> frame, ushort opcode)
    {
        if (opcode is LoginAuthProtocol.AuthenticateRequestOpcode or LoginAuthProtocol.AuthenticateResponseOpcode)
            return $"{ProtocolInspector.ToHex(frame[..Math.Min(8, frame.Length)])}[鐪佺暐璁よ瘉瀵嗘枃鎴栫エ鎹甝";
        if (opcode is LoginAuthProtocol.PublicKeyRequestOpcode or LoginAuthProtocol.PublicKeyResponseOpcode)
            return $"{ProtocolInspector.ToHex(frame[..Math.Min(8, frame.Length)])}[省略认证公钥数据]";

        if ((opcode is 0x2713 or 0x2719 or 0x2732) && frame.Length >= 8)
        {
            var contextOmittedLength = frame.Length - 8;
            return contextOmittedLength > 0
                ? $"{ProtocolInspector.ToHex(frame[..8])}[省略 {contextOmittedLength} 字节登录/账号客户端上下文]"
                : ProtocolInspector.ToHex(frame);
        }

        if (opcode == 0xC351 && frame.Length >= 8)
            return FormatIdentityFrameForLog(frame, 0, WorldConnectPayloadLength, "世界连接账号槽填充");

        if (opcode == 0xC353 && frame.Length >= 8)
            return FormatIdentityFrameForLog(frame, 0, MainGuideIdentityLength, "账号槽填充及客户端本地 PetCode 上下文");

        if (opcode == 0xC376 && frame.Length >= 8)
            return FormatIdentityFrameForLog(frame, ProfileQueryIdentityOffset, ProfileQueryIdentityLength, "角色资料查询角色名槽填充");

        if (opcode == 0x2730 && frame.Length >= 12)
        {
            const int legacyLoggedLength = 12; // native header + account/UIN only
            var legacyOmittedLength = frame.Length - legacyLoggedLength;
            return legacyOmittedLength > 0
                ? $"{ProtocolInspector.ToHex(frame[..legacyLoggedLength])}[省略 {legacyOmittedLength} 字节登录令牌槽及未定义填充]"
                : ProtocolInspector.ToHex(frame);
        }

        if (opcode != 0xEB8F || frame.Length < 12)
            return ProtocolInspector.ToHex(frame);

        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(8, 4));
        var validDataLength = declaredLength <= AntiBotSmallDataCapacity
            ? Math.Min((int)declaredLength, frame.Length - 12)
            : 0;
        var loggedLength = 12 + validDataLength;
        var omittedLength = frame.Length - loggedLength;
        return omittedLength > 0
            ? $"{ProtocolInspector.ToHex(frame[..loggedLength])}[省略 {omittedLength} 字节未定义填充]"
            : ProtocolInspector.ToHex(frame);
    }

    private static string FormatIdentityFrameForLog(
        ReadOnlySpan<byte> frame,
        int identityPayloadOffset,
        int identitySlotLength,
        string omittedDescription)
    {
        var identityFrameOffset = 8 + identityPayloadOffset;
        var availableIdentityLength = Math.Min(identitySlotLength, Math.Max(0, frame.Length - identityFrameOffset));
        var identitySlot = frame.Slice(identityFrameOffset, availableIdentityLength);
        var terminator = identitySlot.IndexOf((byte)0);
        var loggedLength = terminator >= 0
            ? identityFrameOffset + terminator + 1
            : Math.Min(identityFrameOffset, frame.Length);
        var omittedLength = frame.Length - loggedLength;
        return omittedLength > 0
            ? $"{ProtocolInspector.ToHex(frame[..loggedLength])}[省略 {omittedLength} 字节{omittedDescription}]"
            : ProtocolInspector.ToHex(frame);
    }

    private static bool SessionIdentityMatches(string identity, ConnectionSession session)
        => string.Equals(identity, session.AccountId.ToString(), StringComparison.Ordinal)
            || string.Equals(identity, session.Username, StringComparison.OrdinalIgnoreCase)
            || string.Equals(identity, session.Character?.Name, StringComparison.OrdinalIgnoreCase);

    private static bool TryDecodeWorldIdentity(byte[] payload, out string identity)
        => TryDecodeGbkIdentity(
            payload.AsSpan(0, Math.Min(WorldConnectPayloadLength, payload.Length)),
            out identity,
            out _);

    private static bool TryDecodeGbkIdentity(
        ReadOnlySpan<byte> slot,
        out string identity,
        out int encodedLength)
    {
        identity = string.Empty;
        encodedLength = 0;
        var terminator = slot.IndexOf((byte)0);
        if (terminator <= 0)
            return false;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var strictGbk = Encoding.GetEncoding(
                936,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            identity = strictGbk.GetString(slot[..terminator]);
            encodedLength = terminator;
            return identity.Length > 0 && !identity.Any(char.IsControl);
        }
        catch (DecoderFallbackException)
        {
            identity = string.Empty;
            return false;
        }
    }

    private static bool IsPrintableAscii(byte[] value, int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset > value.Length - length)
            return false;
        for (var index = offset; index < offset + length; index++)
            if (value[index] is < 0x21 or > 0x7E)
                return false;
        return true;
    }

    private static bool IsArenaAdapterOpcode(ushort opcode) => opcode is
        0x03E8 or 0x044C or 0x0514 or 0x0578 or 0x05DC or 0x0640
        or 0xCB21 or 0xCB22 or 0xCB23
        or 0xCF09 or 0xCF0B or 0xCF0D or 0xCF0F or 0xCF11 or 0xCF13 or 0xCF17 or 0xCF19 or 0xCF1D
        or 0xCF6C or 0xCF6E or 0xCF70 or 0xCF73 or 0xCF75 or 0xCF77
        or 0xCF7B or 0xCF7D or 0xCF7F or 0xCF81 or 0xCF83 or 0xCF85 or 0xCF89 or 0xCF8B or 0xCF8D
        or 0xCF93 or 0xCF97 or 0xCF99
        or 0xCFD1 or 0xCFD3 or 0xCFD5 or 0xCFD7 or 0xCFD9 or 0xCFE5 or 0xCFEB
        or 0xD00D or 0xD00F or 0xD034 or 0xD036;

    private static string? GetRequiredInboundChannel(ushort opcode) => opcode switch
    {
        0x2713 or 0x2717 or 0x2719 or 0x271B or 0x2725 or 0x2730 or 0x2732 => "GameAdapter",
        0xCF0B or 0xCF0D or 0xCF11 or 0xCF13 or 0xCF17 or 0xCF19
            or 0xCF85 or 0xCF89 or 0xCF97 or 0xCFE5 or 0xD036 => "ArenaAdapter",
        0x03E8 or 0x044C or 0x0514 or 0x0578 or 0x05DC or 0x0640 or 0xC351 or 0xC353 or 0xC354 or 0xC358 or 0xC365 or 0xC367 or 0xC369 or 0xC36C or 0xC376 or 0xC387 or 0xC388 or 0xC578 or 0xC57D or 0xC57F or 0xC581 or 0xC583 or 0xC584 or 0xC585 or 0xC586 or 0xC587 or 0xCB21 or 0xCB22 or 0xCB23 or 0xCF09 or 0xCF0F or 0xCF15 or 0xCF1D or 0xCF6C or 0xCF6E or 0xCF70 or 0xCF73 or 0xCF75 or 0xCF77 or 0xCF7B or 0xCF7D or 0xCF7F or 0xCF87 or 0xCF8B or 0xCF8D or 0xCF93 or 0xCF95 or 0xCF99 or 0xCF9B or 0xD00D or 0xD00F or 0xD011 or 0xD034
            or 0xCFD1 or 0xCFD3 or 0xCFD5 or 0xCFD9 or 0xCFEB
            or 0xC378 or 0xC37A or 0xC3CB or 0xC3CD or 0xC3CF or 0xC3D1 or 0xC3D4 or 0xC3D6 or 0xC3D8 or 0xC3E7 or 0xC3E9 or 0xC3ED or 0xC3EF or 0xC3F3 or 0xC3FB or 0xC3FF or 0xC401 or 0xC431 or 0xC433 or 0xC469 or 0xC46B or 0xC46D or 0xC46F or 0xC47A or 0xC480 or 0xC491
            or 0xC38D or 0xC38F or 0xC392 or 0xC398 or 0xC3AB or 0xC3AD or 0xC405 or 0xC409 or 0xC40B or 0xC40F or 0xC411 or 0xC417 or 0xC419 or 0xC41B or 0xC423 or 0xC42D or 0xC437 or 0xC439 or 0xC43B or 0xC42F or 0xC44B or 0xC44D or 0xC44F or 0xC451 or 0xC453 or 0xC473 or 0xC475 or 0xC47D or 0xC4AF or 0xC4B1 or 0xC4B3 or 0xC4B7 or 0xC4B8 or 0xC4BA or 0xC4BC or 0xC4BE or 0xC4BF or 0xC4E0 or 0xC4E1 or 0xC4E3 or 0xC4E5 or 0xC4E7 or 0xC4EA or 0xC595 or 0xC597 or 0xC599 or 0xC59B or 0xC59E or 0xC5AA or 0xC5B0 or 0xC5B2 or 0xC5B4 or 0xC5B6
            or 0xEB29 or 0xEB8F => "WorldAdapter",
        _ => null
    };

    private static string GetProtocolStage(ushort opcode) => opcode switch
    {
        0x2713 => "login",
        0x2717 => "character-create",
        0x271B => "channel-list",
        0x2725 => "friend-recommendation",
        0x2730 => "legacy-tencent-login",
        0x2732 => "restriction-check",
        0xC351 => "world-connect",
        0xC354 => "load-necessity",
        0xC358 => "oz-village-enter",
        0xC388 => "arena-adapter-endpoint",
        0xC378 => "box-info",
        0xC3CF => "avatar-item-delete",
        0xC3D1 => "face-coupon-use",
        0xC38D => "apartment-enter",
        0xC38F => "apartment-user-info",
        0xC392 => "apartment-object-info",
        0xC398 => "apartment-recommend-count",
        0xC365 => "town-enter",
        0xC367 => "room-enter",
        0xC353 => "main-guide-complete",
        0xC376 => "profile-query",
        0xC369 => "town-user-info",
        0xC36C => "town-page-complete",
        0xC37A => "shop-move",
        0xC3AB => "village-shop-enter",
        0xC3AD => "shop-user-info",
        0xC405 => "apartment-interior-catalog",
        0xC411 => "apartment-interior-save",
        0xC423 => "apartment-interior-info",
        0xC578 => "mentor-room-create",
        0xC57D => "mentor-advertisement-list",
        0xC57F => "mentor-advertise",
        0xC581 => "mentor-advertise-stop",
        0xC583 => "couple-ring-request",
        0xC584 => "couple-ring-response",
        0xC585 => "couple-separation-request",
        0xC586 => "couple-separation-response",
        0xC451 => "pet-charge-list",
        0xC453 => "pet-charge",
        0xC40B => "interior-shop-purchase",
        0xC40F => "interior-item-delete",
        0xC417 => "interior-wishlist-load",
        0xC419 => "interior-wishlist-add",
        0xC41B => "interior-wishlist-delete",
        0xC42D => "shop-owned-item-state",
        0xC433 => "game-item-delete",
        0xC437 => "shop-wishlist-load",
        0xC439 => "shop-wishlist-add",
        0xC43B => "shop-wishlist-delete",
        0xC3CD => "nana-shop-purchase",
        0xC3D4 => "nana-wishlist-load",
        0xC3D6 => "nana-wishlist-add",
        0xC3D8 => "nana-wishlist-delete",
        0xC431 => "shop-purchase",
        0xC46F => "special-token-purchase",
        0xC491 => "special-card-purchase",
        0xC47A => "shop-gift",
        0xC46B => "special-token-delete",
        0xC46D => "special-token-use",
        0xC3E7 => "card-list",
        0xC3E9 => "card-summon-info",
        0xC3ED => "card-item-synthesis",
        0xC3EF => "card-item-synthesis-finish",
        0xC3F3 => "card-sell",
        0xC3FB => "card-guide-step",
        0xC3FF => "skill-upgrade",
        0xC401 => "skill-slot-save",
        0xC480 => "inventory-expansion-use",
        0xC3CB or 0xC409 or 0xC42F or 0xC44B or 0xC469 or 0xC473 => "inventory-load",
        0xC475 => "cash-inbox-claim",
        0xC44F => "pet-change",
        0xC44D => "pet-delete",
        0xC47D => "inventory-equipment-save",
        0xC4AF => "trade-invite",
        0xC4B1 => "trade-agreement",
        0xC4B3 => "trade-room-create",
        0xC4B7 => "trade-room-enter",
        0xC4B8 => "trade-peer-info",
        0xC4BA => "trade-offer-update",
        0xC4BC => "trade-ready",
        0xC4BE => "trade-cancel",
        0xC4BF => "trade-confirm",
        0xC4E0 => "party-invite",
        0xC4E1 => "party-invite-result",
        0xC4E3 => "party-owner-change",
        0xC4E5 => "party-member-remove",
        0xC4E7 => "party-leave",
        0xC4EA => "scene-transition-notify",
        0xC595 => "task-activate",
        0xC597 => "task-abandon",
        0xC599 => "task-complete",
        0xC59B => "task-list",
        0xC59E => "quest-scroll-purchase",
        0xC5AA => "scene-transition",
        0xC5B0 => "auction-list",
        0xC5B2 => "auction-buy",
        0xC5B4 => "auction-register",
        0xC5B6 => "auction-retrieval",
        0xCB21 => "scene-movement",
        0xCB22 => "scene-emotion",
        0xCB23 => "scene-chat",
        0xCF0B => "arena-entertainment-lobby",
        0xCF13 => "arena-entertainment-ranking",
        0xCF19 => "arena-entertainment-wait-users",
        0xCFE5 => "arena-entertainment-game-data",
        0xCF81 => "arena-entertainment-countdown",
        0xCF83 => "arena-entertainment-value",
        0xCF85 => "arena-entertainment-continue",
        0xCF0D => "arena-lobby-info",
        0xCF11 => "arena-room-list",
        0xCF17 => "arena-ranking",
        0xCF89 => "arena-pvp-result",
        0xCF97 => "arena-pvp-finish",
        0xD036 => "arena-mode-map",
        0xCF09 => "game-session-connect",
        0xCF0F => "dungeon-room-list",
        0xCF15 => "dungeon-stage-records",
        0xCF1D => "dungeon-game-disconnect",
        0xC587 => "dungeon-room-local-identity",
        0xCF6C => "dungeon-create-game",
        0xCF6E => "dungeon-gameplay-enter",
        0xCF70 => "dungeon-room-user-info",
        0xCF73 => "dungeon-room-leave",
        0xCF75 => "dungeon-lobby-room-enter",
        0xCF77 => "dungeon-quick-enter",
        0xCF7B => "dungeon-slot-change",
        0xCF7D => "dungeon-ready-state",
        0xCF7F => "dungeon-game-start",
        0xCF87 => "dungeon-end-game",
        0xCF8B => "dungeon-resetting",
        0xCFD1 => "dungeon-p2p-info",
        0xCFD3 => "dungeon-p2p-peers",
        0xCFD5 => "dungeon-p2p-protocol",
        0xCFD9 => "dungeon-multicast-init",
        0xCFEB => "dungeon-game-data",
        0x03E8 => "dungeon-peer-player-state",
        0x044C => "dungeon-shooting-sync",
        0x0514 => "dungeon-peer-trigger",
        0x0578 => "dungeon-peer-loading-heartbeat",
        0x05DC => "dungeon-peer-entity-state-6",
        0x0640 => "dungeon-peer-entity-state-5",
        0xCF8D => "dungeon-owner-kick",
        0xCF95 => "dungeon-retry",
        0xCF99 => "dungeon-surrender-check",
        0xCF9B => "dungeon-skill-use",
        0xD00D => "dungeon-collision",
        0xD00F => "game-object-event",
        0xD011 => "dungeon-boss-recording",
        0xD034 => "dungeon-pickup",
        0xEB29 => "gameguard-auth",
        0xEB8F => "antibot-telemetry",
        _ => "unknown"
    };

}
