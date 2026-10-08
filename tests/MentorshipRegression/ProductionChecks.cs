using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static partial class Program
{
    private static T InvokeMentorship<T>(NetworkAdapterService service, string method, params object?[] arguments)
        => (T)typeof(NetworkAdapterService).GetMethod(method, PrivateInstance)!.Invoke(service, arguments)!;

    private static Task<byte[]?> MentorCommand(Fixture f, object session, string command)
    {
        var payload = new byte[48];
        Encoding.GetEncoding(936).GetBytes("Teacher").CopyTo(payload, 0);
        Encoding.GetEncoding(936).GetBytes(command).CopyTo(payload, 16);
        return Dispatch(f, session, 0xCB25, payload);
    }

    private static async Task CheckProductionMentorshipAsync()
    {
        await using var f = await Fixture.CreateAsync();
        f.Service.MentorshipRules = MentorshipPolicy.Production;
        var rules = f.Service.MentorshipRules;
        Check(rules.Courses.Count == 1 && rules.GraduationReward is not null, "production course and graduation award definitions resolve");
        Check(await MentorCommand(f, f.Teacher, "/mentor invite Student") is not null, "private command creates a teacher invitation");
        var request = await f.ScalarAsync("SELECT MAX(Id) FROM MentorshipRequests");
        Check(request > 0, "production invitation persists its identity");
        Check(await MentorCommand(f, f.Student, "/mentor accept " + request) is not null, "the addressed student confirms the production invitation");
        var relation = (await f.Database.GetMentorshipRelationsAsync(Actor(f.Student))).Single();
        Check(relation.TeacherCharacterId == Character(f.Teacher).Id, "production confirmation establishes the owned relation");
        await MentorCommand(f, f.Teacher, "/mentor graduate " + relation.Id);
        Check((await f.Database.GetMentorshipRelationsAsync(Actor(f.Student))).Single().State == MentorshipRelationState.Active,
            "graduation requires completed course receipts");
        await using var pool = new NativeDungeonPool("unused", f.Root);
        await using var teacherNative = new NativeDungeonClient(_ => Task.CompletedTask);
        await using var studentNative = new NativeDungeonClient(_ => Task.CompletedTask);
        var course = rules.Courses.Single();
        foreach (var (session, native) in new[] { (f.Teacher, teacherNative), (f.Student, studentNative) })
        {
            Set(session, "NativeDungeon", native);
            Set(session, "NativeLease", new NativeDungeonPool.Lease(pool, "mentorship-course", 61200));
            Set(session, "NativeDungeonSelectionValid", true);
            Set(session, "NativeDungeonEpisode", checked((byte)course.Episode));
            Set(session, "NativeDungeonDungeon", checked((byte)course.DungeonBit));
            Set(session, "NativeDungeonStage", (byte)0);
        }
        Check(await InvokeMentorship<Task<uint>>(f.Service, "ScaleMentorshipExperienceAsync",
                f.Student, 100u, CancellationToken.None) == 150,
            "active teacher and student receive one hundred fifty percent experience in the same dungeon");
        Check(await InvokeMentorship<Task<uint>>(f.Service, "ScaleMentorshipExperienceAsync",
                f.Teacher, 101u, CancellationToken.None) == 151,
            "teacher also receives the multiplier with fractional experience rounded down");
        Check(await InvokeMentorship<Task<uint>>(f.Service, "ScaleMentorshipExperienceAsync",
                f.Student, uint.MaxValue, CancellationToken.None) == uint.MaxValue,
            "mentorship experience saturates without integer overflow");
        Set(f.Teacher, "NativeDungeonDeathLatched", true);
        Check(await InvokeMentorship<Task<uint>>(f.Service, "ScaleMentorshipExperienceAsync",
                f.Student, 100u, CancellationToken.None) == 100,
            "a dead teacher cannot provide the shared experience bonus");
        Set(f.Teacher, "NativeDungeonDeathLatched", false);
        Set(f.Student, "NativeDungeonLogicalDifficulty", (byte)1);
        Check(await InvokeMentorship<Task<uint>>(f.Service, "ScaleMentorshipExperienceAsync",
                f.Student, 100u, CancellationToken.None) == 100,
            "different dungeon difficulties preserve base experience");
        Set(f.Student, "NativeDungeonLogicalDifficulty", (byte)0);
        Set(f.Student, "NativeDungeonStage", (byte)1);
        Check(await InvokeMentorship<Task<uint>>(f.Service, "ScaleMentorshipExperienceAsync",
                f.Student, 100u, CancellationToken.None) == 100,
            "different dungeon stages preserve base experience");
        Set(f.Student, "NativeDungeonStage", (byte)0);
        Set(f.Teacher, "NativeBattleEpoch", 10L);
        Set(f.Student, "NativeBattleEpoch", 2L);
        string Round(object session) => InvokeMentorship<string>(f.Service, "GetNativeMentorshipSettlementKey", session);
        var key = Round(f.Teacher);
        Check(key == Round(f.Student), "native course identity is room owned across unequal connection epochs");
        InvokeMentorship<object?>(f.Service, "AdvanceNativeMentorshipRound", f.Teacher);
        Check(Round(f.Teacher) != key && Round(f.Teacher) == Round(f.Student), "accepted continuation creates one fresh course round for all members");
        key = Round(f.Teacher);
        Task<MentorshipSettlementResult> Clear(object session, bool cleared = true)
            => InvokeMentorship<Task<MentorshipSettlementResult>>(f.Service, "RecordMentorshipClearAsync", session,
                key, course.Episode, course.DungeonBit, cleared, CancellationToken.None);
        Check((await Clear(f.Teacher)).CompletedRelations.Count == 0, "one native clear receipt waits for the partner");
        Check((await Clear(f.Teacher)).Code == MentorshipResultCode.AlreadyCompleted, "repeated native clear reuses its receipt");
        Check((await Clear(f.Student, false)).Code == MentorshipResultCode.Ineligible, "clear eligibility is checked before course certification");
        Set(f.Student, "NativeDungeonLogicalDifficulty", (byte)1);
        Check((await Clear(f.Student)).Code == MentorshipResultCode.Ineligible, "course participants share the complete stage selection");
        Set(f.Student, "NativeDungeonLogicalDifficulty", (byte)0);
        Check((await Clear(f.Student)).CompletedRelations.Single().CompletedLessonCount == 1,
            "two successful native results certify the production course");
        Check((await f.Service.GraduateMentorshipAsync(Id(f.Teacher), relation.Id)).Code == MentorshipResultCode.Ineligible,
            "graduation waits for the town inventory boundary");
        foreach (var session in new[] { f.Teacher, f.Student })
        {
            Set(session, "NativeDungeon", null);
            Set(session, "NativeLease", null);
        }
        Check((await f.Service.GraduateMentorshipAsync(Id(f.Teacher), relation.Id)).Code == MentorshipResultCode.Ineligible,
            "completed coursework still requires student level twenty");
        await f.ExecuteAsync($"UPDATE Characters SET Level = 20, Experience = {CharacterProgression.ExperienceRequiredForLevel(20)} WHERE Id = $id", ("$id", Character(f.Student).Id));
        Character(f.Student).Level = 20;
        Character(f.Student).Experience = CharacterProgression.ExperienceRequiredForLevel(20);
        Check((await Dispatch(f, f.Teacher, 0xC578, [])) is { Length: 288 },
            "teacher profile completes eligible level-twenty graduation");
        var graduated = (await f.Database.GetMentorshipRelationsAsync(Actor(f.Student), true)).Single();
        Check(graduated.State == MentorshipRelationState.Graduated && graduated.GraduationRewardGranted,
            "production graduation persists its award disposition");
        var reward = rules.GraduationReward!;
        var student = (await f.Database.GetCharacterByIdAsync(Character(f.Student).Id))!;
        var granted = student.Items.Single(item => item.ItemCode == reward.ItemCode).Quantity;
        Check(granted == reward.Quantity, "graduation grants the defined pet material quantity");
        Check(Character(f.Student).Items.Single(item => item.ItemCode == reward.ItemCode).Quantity == granted,
            "the active student inventory projects the committed graduation award");
        await MentorCommand(f, f.Teacher, "/mentor graduate " + relation.Id);
        Check((await f.Database.GetCharacterByIdAsync(student.Id))!.Items.Single(item => item.ItemCode == reward.ItemCode).Quantity == granted,
            "repeated graduation preserves a single award");
        Check(await f.ScalarAsync("SELECT COUNT(*) FROM MentorshipGraduationAwards") == 1,
            "production award receipt is unique");
    }
}
