from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
OUTPUT = HERE / "artifacts" / "integration"
OUTPUT.mkdir(parents=True, exist_ok=True)


def replace_once(source, old, new):
    if source.count(old) != 1:
        raise RuntimeError("Shared integration anchor must be unique.")
    return source.replace(old, new, 1)


def section(source, begin, end, replacement):
    start = source.index(begin)
    finish = source.index(end, start)
    return source[:start] + replacement + source[finish:]


database = (ROOT / "managed/Services/DatabaseService.cs").read_text(encoding="utf-8-sig")
if "await InitializeMentorshipAsync(cancellationToken);" not in database:
    anchor = "        await RepairApartmentInventoryPlacementsAsync(cancellationToken: cancellationToken);"
    database = replace_once(database, anchor, anchor + "\n        await InitializeMentorshipAsync(cancellationToken);\n        await ExpireMentorshipRequestsAsync(cancellationToken);")
if "var mentorStudents = 0;" in database:
    database = section(database, "        var mentorStudents = 0;", "        bool ObjectiveMet(",
        "        var mentorStudents = checked((int)await CountMentorshipStudentsAsync(\n            connection, transaction, characterId, cancellationToken));\n\n")
if "Use session-owned mentorship requests." not in database:
    database = section(database, "    public async Task<long> RecordMentorInteractionAsync(",
        "    public async Task CompleteMentorInteractionAsync(", """    public Task<long> RecordMentorInteractionAsync(
        ushort requestOpcode, long requesterCharacterId, long targetCharacterId,
        uint lessonCode, byte targetUid, CancellationToken cancellationToken = default)
        => Task.FromException<long>(new NotSupportedException("Use session-owned mentorship requests."));

""")
if "Legacy interactions cannot establish mentorship." not in database:
    start = database.index("    public async Task CompleteMentorInteractionAsync(")
    begin = database.index("        await using var connection", start)
    database = database[:begin] + "        if (status == MentorProtocol.Accepted)\n            throw new NotSupportedException(\"Legacy interactions cannot establish mentorship.\");\n" + database[begin:]
(OUTPUT / "DatabaseService.cs").write_text(database, encoding="utf-8")

adapter = (ROOT / "managed/Services/NetworkAdapterService.cs").read_text(encoding="utf-8-sig")
if "HandleMentorshipFrameAsync(" not in adapter:
    adapter = section(adapter, "            case MentorProtocol.AdvertiseRequestOpcode:", "            case 0xEB29:", """            case MentorProtocol.CreateSchoolingRoomRequestOpcode:
            case MentorProtocol.ListRequestOpcode:
            case MentorProtocol.AdvertiseRequestOpcode:
            case MentorProtocol.StopAdvertisingRequestOpcode:
                return await HandleMentorshipFrameAsync(frame, opcode, payload, session, token);

            case CoupleProtocol.RingRequestOpcode:
            case CoupleProtocol.RingResponseOpcode:
            case CoupleProtocol.SeparationRequestOpcode:
            case CoupleProtocol.SeparationResponseOpcode:
                return await HandleCoupleRequestAsync(frame, opcode, payload, session, channel, remote, token);

""")
if "await CleanupMentorshipConnectionAsync(session);" not in adapter:
    adapter = section(adapter, "    private async Task CleanupMentorSessionAsync(ConnectionSession session)",
        "    private async Task HealthRecoveryLoopAsync(", """    private async Task CleanupMentorSessionAsync(ConnectionSession session)
    {
        await CleanupMentorshipConnectionAsync(session);
        lock (_coupleGate)
        {
            foreach (var key in _couplePendingRequests
                         .Where(pair => pair.Value.Requester.SessionId == session.SessionId
                                        || pair.Value.Responder.SessionId == session.SessionId)
                         .Select(pair => pair.Key).ToArray())
                _couplePendingRequests.Remove(key);
        }
    }

""")
if "_database.ExpireMentorshipRequestsAsync()" not in adapter:
    anchor = "        AccountStateChanged?.Invoke();\n        MentorStateChanged?.Invoke();\n        if (items.Count > 0)"
    adapter = replace_once(adapter, anchor, "        await _database.ExpireMentorshipRequestsAsync();\n" + anchor)
(OUTPUT / "NetworkAdapterService.cs").write_text(adapter, encoding="utf-8")
print("MENTORSHIP_INTEGRATION_PREPARED")
