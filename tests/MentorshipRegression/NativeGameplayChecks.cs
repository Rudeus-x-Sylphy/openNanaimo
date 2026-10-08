using System.Buffers.Binary;
using System.Collections;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckAutomaticLevelGraduationAsync()
    {
        foreach (var level in new[] { 19, 20, 21 })
        {
            await using var f = await Fixture.CreateAsync();
            f.Service.MentorshipRules = MentorshipPolicy.Production;
            await f.ExecuteAsync($"UPDATE Characters SET Level=18,Experience={CharacterProgression.ExperienceRequiredForLevel(18)} WHERE Id=$id", ("$id", Character(f.Student).Id));
            Character(f.Student).Level = 18;
            Character(f.Student).Experience = CharacterProgression.ExperienceRequiredForLevel(18);
            var request = await f.Service.RequestMentorshipAsync(Id(f.Teacher), Character(f.Student).Id, MentorshipDirection.TeacherInvitation);
            Check(request.Success, "level eighteen invitation");
            var accepted = await f.Service.RespondMentorshipAsync(Id(f.Student), request.Request!.Id, true);
            Check(accepted.Success, "level eighteen relation established");
            await f.ExecuteAsync("UPDATE Characters SET Level=$level,Experience=$exp WHERE Id=$id", ("$level", level), ("$exp", CharacterProgression.ExperienceRequiredForLevel(level)), ("$id", Character(f.Student).Id));
            // The student's next profile read uses persisted level even with a stale session and offline teacher.
            f.RemovePresence(f.Teacher);
            var frame = (await Dispatch(f, f.Student, 0xC578, []))!;
            var relation = (await f.Database.GetMentorshipRelationsAsync(Actor(f.Student), true)).Single();
            Check(relation.State == (level >= 20 ? MentorshipRelationState.Graduated : MentorshipRelationState.Active), "level-only graduation threshold");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(10)) == 3, "graduated student profile retains the original teacher");
            Check(!relation.GraduationRewardGranted, "course reward retains its independent completion condition");
            await Dispatch(f, f.Student, 0xC578, []);
            await InvokeMentorship<Task>(f.Service, "ReconcileLevelMentorshipsAsync", f.Student, CancellationToken.None);
            Check(await f.ScalarAsync("SELECT COUNT(*) FROM MentorshipGraduations") == (level >= 20 ? 1 : 0), "automatic graduation idempotent");
        }
        await using var busy = await Fixture.CreateAsync();
        busy.Service.MentorshipRules = MentorshipPolicy.Production;
        var pending = await busy.Service.RequestMentorshipAsync(Id(busy.Teacher), Character(busy.Student).Id, MentorshipDirection.TeacherInvitation);
        Check((await busy.Service.RespondMentorshipAsync(Id(busy.Student), pending.Request!.Id, true)).Success, "busy relation established");
        await busy.ExecuteAsync($"UPDATE Characters SET Level=21,Experience={CharacterProgression.ExperienceRequiredForLevel(21)} WHERE Id=$id", ("$id", Character(busy.Student).Id));
        Set(busy.Student, "TownSceneActive", false);
        await InvokeMentorship<Task>(busy.Service, "ReconcileLevelMentorshipsAsync", busy.Teacher, CancellationToken.None);
        Check((await busy.Database.GetMentorshipRelationsAsync(Actor(busy.Student))).Count == 1, "graduation waits for both town boundaries");
        Set(busy.Student, "TownSceneActive", true);
        await InvokeMentorship<Task>(busy.Service, "RefreshSessionCharacterAsync", busy.Student, CancellationToken.None);
        Check((await busy.Database.GetMentorshipRelationsAsync(Actor(busy.Student))).Count == 0, "town refresh closes level graduation");
    }

    private static async Task CheckNativeMentorshipAsync()
    {
        await CheckAutomaticLevelGraduationAsync();
        await CheckRecruitmentRefreshAsync();
        await using var f = await Fixture.CreateAsync();
        f.Service.MentorshipRules = MentorshipPolicy.Production;
        var teacher = Character(f.Teacher); var student = Character(f.Student);
        var request = new byte[24]; request[1] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), WireIdentityAllocator.GetSceneEntityId(student.Id));
        var pending = (IList)Get(f.Teacher, "PendingBroadcasts")!; pending.Clear();
        Check(await Dispatch(f, f.Teacher, 0xC576, request) is null && pending.Count == 1,
            "native teacher invitation reaches the level-one student");
        var notice = (byte[])Get(pending[0]!, "Payload")!;
        Check(notice[1] == 1 && notice[2] == 30 && BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(4))
            == WireIdentityAllocator.GetSceneEntityId(teacher.Id), "native invitation publishes authoritative peer identity and level");
        var answer = new byte[24]; answer[0] = 10;
        BinaryPrimitives.WriteUInt16LittleEndian(answer.AsSpan(6), WireIdentityAllocator.GetSceneEntityId(teacher.Id));
        var reply = await Dispatch(f, f.Student, 0xC577, answer);
        Check(reply is { Length: 32 } && reply[8] == 10, "native student acceptance completes the owned relation");
        Check(await Dispatch(f, f.Student, 0xC577, answer) is null,
            "duplicate native acceptance preserves one relationship");
        var profile = (await Dispatch(f, f.Teacher, 0xC578, []))!;
        Check(profile.Length == 288 && BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(10)) == 2
            && profile[12] == 1 && profile[252] == 30
            && BinaryPrimitives.ReadUInt32LittleEndian(profile.AsSpan(264)) == 1,
            "native teacher profile carries the student row and full character summary");
        var studentProfile = (await Dispatch(f, f.Student, 0xC578, []))!;
        Check(studentProfile.Length == 288 && BinaryPrimitives.ReadUInt16LittleEndian(studentProfile.AsSpan(10)) == 3
            && studentProfile[12] == 30, "native student profile shows its teacher");
        var advertise = await Dispatch(f, f.Teacher, 0xC57F, []);
        Check(advertise is { Length: 12 } && Character(f.Teacher).Hans == 900,
            "teacher with a student can recruit another student for one hundred coins");
        var listed = (await Dispatch(f, f.Other, 0xC57D, new byte[4]))!;
        Check(listed[16] == 30 && listed[17] == teacher.DungeonGrade,
            "advertisement level and title occupy their native fields");
        await Dispatch(f, f.Teacher, 0xC57F, []);
        Check(Character(f.Teacher).Hans == 900, "active advertisement is charged once");
    }

    private static async Task CheckRecruitmentRefreshAsync()
    {
        await using var f = await Fixture.CreateAsync();
        f.Service.MentorshipRules = MentorshipPolicy.Production;
        var teacher = Character(f.Teacher);
        async Task SaveGrade(byte grade)
        {
            var state = new byte[NativeDungeonState.Size];
            BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(NativeDungeonState.DungeonGradeOffset), grade);
            await f.ExecuteAsync("INSERT INTO NativeDungeonProfiles(CharacterId,State) VALUES($id,$state) "
                + "ON CONFLICT(CharacterId) DO UPDATE SET State=excluded.State",
                ("$id", teacher.Id), ("$state", state));
        }
        Task<byte[]?> List(object session) => Dispatch(f, session, MentorProtocol.ListRequestOpcode, new byte[4]);
        static uint Count(byte[] frame) => BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12));
        Check(Count((await List(f.Teacher))!) == 0, "recruitment list begins empty");
        await f.ExecuteAsync("CREATE TABLE IF NOT EXISTS NativeDungeonProfiles("
            + "CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id), State BLOB NOT NULL)");
        await SaveGrade(16);
        var pending = (IList)Get(f.Teacher, "PendingBroadcasts")!;
        var response = (await Dispatch(f, f.Teacher, MentorProtocol.AdvertiseRequestOpcode, []))!;
        Check(response.Length == 12 && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xC580
            && BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8)) == 0,
            "advertisement acknowledges the committed activation");
        Check(Character(f.Teacher).Hans == 900, "activation charges one hundred coins");
        Check(pending.Count == 0, "activation does not broadcast unsolicited lists");
        var own = (await List(f.Teacher))!;
        Check(Count(own) == 1 && own.Length == 36, "immediate owner refresh includes the new advertisement");
        Check(own[16] == 30 && own[17] == 16, "owner list preserves distinct level and dungeon grade");
        Check(MentorProtocol.TryReadPeerName(own.AsSpan(20), out var name) && name == teacher.Name,
            "owner refresh identifies the advertised teacher");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(own.AsSpan(18)) == 0,
            "ungraduated teacher keeps a zero graduation count beside a nonzero grade");
        var observer = (await List(f.Student))!;
        Check(observer.AsSpan(8).SequenceEqual(own.AsSpan(8)), "owner and student see the same recruitment entry");
        var persisted = await f.Database.GetMentorshipAdvertisingCharactersAsync(Actor(f.Teacher), f.Service.MentorshipRules);
        Check(persisted.Single().DungeonGrade == 16, "advertisement query loads the persisted dungeon grade");
        Check(Count((await List(f.Visitor))!) == 0, "recruitment remains channel scoped");
        Check((await f.Service.RequestMentorshipAsync(Id(f.Teacher), teacher.Id,
            MentorshipDirection.StudentApplication)).Code == MentorshipResultCode.Ineligible,
            "a visible own advertisement cannot establish self-teaching");
        await SaveGrade(7);
        Check(Character(f.Teacher).DungeonGrade == 16, "session retains its earlier grade before refresh");
        observer = (await List(f.Student))!;
        Check(observer[17] == 7, "list reloads the saved grade rather than a stale session value");
        var profile = (await Dispatch(f, f.Teacher, 0xC578, []))!;
        Check(profile[253] == observer[17], "recruitment and relationship profile use the same grade");
        await Dispatch(f, f.Teacher, MentorProtocol.AdvertiseRequestOpcode, []);
        Check(Character(f.Teacher).Hans == 900 && Count((await List(f.Teacher))!) == 1,
            "repeated activation neither charges twice nor duplicates the row");
        await Dispatch(f, f.Other, MentorProtocol.AdvertiseRequestOpcode, []);
        own = (await List(f.Teacher))!;
        Check(Count(own) == 2 && own[17] == 7 && own[37] == 0,
            "multiple advertisers retain independent grades and include the requester");
        await Dispatch(f, f.Teacher, MentorProtocol.StopAdvertisingRequestOpcode, []);
        Check(Count((await List(f.Teacher))!) == 1 && Count((await List(f.Student))!) == 1,
            "stopping recruitment removes the row on the next owner and student requests");
        await Dispatch(f, f.Teacher, MentorProtocol.AdvertiseRequestOpcode, []);
        Check(Character(f.Teacher).Hans == 800 && Count((await List(f.Teacher))!) == 2,
            "a new activation charges once and immediately restores the row");
        await Dispatch(f, f.Teacher, MentorProtocol.StopAdvertisingRequestOpcode, []);
        await f.ExecuteAsync("UPDATE Characters SET Hans=99 WHERE Id=$id", ("$id", teacher.Id));
        Check(await Dispatch(f, f.Teacher, MentorProtocol.AdvertiseRequestOpcode, []) is null,
            "insufficient coins do not acknowledge recruitment success");
        Check(Count((await List(f.Teacher))!) == 1
            && (await f.Database.GetCharacterByIdAsync(teacher.Id))!.Hans == 99,
            "failed activation preserves balance and list state");
        Check(pending.Count == 0, "recruitment refresh remains request driven");
    }

}
