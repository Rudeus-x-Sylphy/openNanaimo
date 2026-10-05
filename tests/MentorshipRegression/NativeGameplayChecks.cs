using System.Buffers.Binary;
using System.Collections;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static async Task CheckNativeMentorshipAsync()
    {
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
}
