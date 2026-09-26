"""Offline execution of apartment lease instructions from an optional external PE.

Run: python -B scripts/test_apartment_lease_client.py --client /path/to/game.exe
Or set NANAIMO_CLIENT_EXE and use unittest discovery. Without an external client
or optional unicorn/pefile dependencies, native tests are explicitly skipped.
No client process is started, inspected, or modified. Synthetic manager, stack,
and C38E fields provide the isolated calling context; the calendar algorithm,
its helpers and getter execute unchanged client instructions without stubs.
This is not complete-handler, UI, server-encoder, or original-client runtime
acceptance. Role checks cover equal time operands, not role dispatch. UTC is a
server convention: these client instructions contain no time-zone conversion.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import unittest

try:
    import pefile
    import unicorn as uc
    from unicorn import x86_const as x86
except ImportError:
    pefile = uc = x86 = None

CLIENT = os.environ.get("NANAIMO_CLIENT_EXE")
RESULTS = []
# Half-open ranges; only these original instruction bytes may execute.
RANGES = {
    "calculator_thunk": (0x406AD2, 0x406AD7),
    "calendar_thunk": (0x415C1C, 0x415C21),
    "date_constructor_thunk": (0x41CFD0, 0x41CFD5),
    "getter_thunk": (0x40C9EB, 0x40C9F0),
    "calculator": (0xA75440, 0xA75599),
    "calendar": (0xA752F0, 0xA75436),
    "date_constructor": (0x834040, 0x83405E),
    "native_memset": (0xB4AE30, 0xB4AEAA),
    "getter": (0x5DB1D0, 0x5DB1E4),
    "village_operands": (0x5346D7, 0x5346F6),
    "interior_operands": (0x5A3D2F, 0x5A3D4E),
    "entry_operands": (0x94CA65, 0x94CA7E),
}
CALLERS = (
    ("village", 0x5346D7, 0x5346F6, 0x190),
    ("interior", 0x5A3D2F, 0x5A3D4E, 0x180),
    ("entry", 0x94CA65, 0x94CA7E, 0x14),
)
# name, full+40 expiry, full+44 current, full+9 role, expected whole days.
SAMPLES = (
    ("owner_14_days", 2026101012, 2026092612, 10, 14),
    ("owner_12_days", 2026100812, 2026092612, 10, 12),
    ("expiry_equal", 2026092612, 2026092612, 10, 0),
    ("past_expiry", 2026092512, 2026092612, 10, 0),
    ("leap_february_14", 2024031012, 2024022512, 10, 14),
    ("leap_day", 2024030112, 2024022812, 10, 2),
    ("ordinary_february", 2025030112, 2025022812, 10, 1),
    ("leap_century_2000", 2000030112, 2000022812, 10, 2),
    ("nonleap_century_2100", 2100030112, 2100022812, 10, 1),
    ("year_boundary_14", 2027010912, 2026122612, 10, 14),
    ("hour_borrow_13", 2026101011, 2026092612, 10, 13),
    ("less_than_day", 2026092711, 2026092612, 10, 0),
    ("greater_hour_same_day", 2026092613, 2026092612, 10, 0),
    ("upper_clamp_99", 2027012612, 2026092612, 10, 99),
    ("visitor_same_14", 2026101012, 2026092612, 30, 14),
    ("visitor_same_12", 2026100812, 2026092612, 30, 12),
    ("no_address_owner_all_zero", 0, 0, 20, 0),
    ("no_address_visitor_all_zero", 0, 0, 40, 0),
)


def require(condition, message):
    if not condition:
        raise AssertionError(message)


class NativeCalendar:
    STACK = 0x20000000
    MANAGER = 0x21000000
    FRAME = 0x22000000
    STOP = 0x23000000

    def __init__(self, path):
        self.data = Path(path).read_bytes()
        self.pe = pefile.PE(data=self.data)
        require(self.pe.FILE_HEADER.Machine == 0x14C, "Expected x86 PE")
        require(self.pe.OPTIONAL_HEADER.ImageBase == 0x400000, "Unexpected image base")
        self.image = self.pe.get_memory_mapped_image()
        self.spans = {}
        for name, (start, end) in RANGES.items():
            blob = self.pe.get_data(start - 0x400000, end - start)
            require(len(blob) == end - start, "Truncated instruction range")
            self.spans[name] = {"start": hex(start), "end_exclusive": hex(end),
                                "sha256": hashlib.sha256(blob).hexdigest()}
        for source, target in ((0x406AD2, 0xA75440), (0x415C1C, 0xA752F0),
                               (0x41CFD0, 0x834040), (0x40C9EB, 0x5DB1D0)):
            blob = self.pe.get_data(source - 0x400000, 5)
            require(blob[0] == 0xE9 and source + 5 + struct.unpack_from("<i", blob, 1)[0] == target,
                    "Unsupported client thunk at " + hex(source))

    def execute(self, sample, caller):
        name, expiry, current, role, expected = sample
        caller_name, start, end, local = caller
        vm = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        size = (self.pe.OPTIONAL_HEADER.SizeOfImage + 0xFFF) & ~0xFFF
        vm.mem_map(0x400000, size)
        vm.mem_write(0x400000, self.image)
        vm.mem_map(self.STACK, 0x10000)
        vm.mem_map(self.MANAGER, 0x10000)
        vm.mem_map(self.FRAME, 0x1000)
        vm.mem_map(self.STOP, 0x1000)
        ebp, esp = self.STACK + 0xF000, self.STACK + 0xD000
        vm.mem_write(0xD869D4, struct.pack("<I", self.MANAGER))
        vm.mem_write(ebp - local, struct.pack("<I", self.FRAME))
        frame = bytearray(0x80)
        struct.pack_into("<HH", frame, 4, len(frame), 0xC38E)
        frame[9] = role
        if expiry:
            frame[32], frame[34], frame[35] = 1, 1, 1
        struct.pack_into("<II", frame, 40, expiry, current)
        vm.mem_write(self.FRAME, bytes(frame))
        # Nonzero sentinels prove zero-result samples actively clear stale days.
        vm.mem_write(self.MANAGER + 0x4F67, b"\xA5\xCC\x5A")
        vm.reg_write(x86.UC_X86_REG_ESP, esp)
        vm.reg_write(x86.UC_X86_REG_EBP, ebp)
        vm.reg_write(x86.UC_X86_REG_EFLAGS, 2)
        preserved = {x86.UC_X86_REG_EBX: 0x13572468,
                     x86.UC_X86_REG_ESI: 0x24681357,
                     x86.UC_X86_REG_EDI: 0x34567812}
        for register, value in preserved.items():
            vm.reg_write(register, value)
        visited, writes = {}, []

        def instruction(machine, address, length, _):
            require(any(a <= address and address + length <= b for a, b in RANGES.values()),
                    "Unexpected execution outside isolated chain: " + hex(address))
            visited[address] = visited.get(address, 0) + 1

        def write(machine, access, address, size, value, _):
            if self.MANAGER <= address < self.MANAGER + 0x10000:
                writes.append({"address": hex(address), "size": size, "value": value})
                require(address == self.MANAGER + 0x4F68 and size == 1,
                        "Unexpected manager write")

        vm.hook_add(uc.UC_HOOK_CODE, instruction)
        vm.hook_add(uc.UC_HOOK_MEM_WRITE, write)
        vm.emu_start(start, end, count=20000)
        require(vm.reg_read(x86.UC_X86_REG_EIP) == end, "Instruction budget exhausted")
        require(vm.reg_read(x86.UC_X86_REG_ESP) == esp, "Caller stack imbalance")
        stored = vm.mem_read(self.MANAGER + 0x4F68, 1)[0]
        vm.mem_write(esp, struct.pack("<I", self.STOP))
        vm.reg_write(x86.UC_X86_REG_ECX, self.MANAGER)
        vm.emu_start(0x40C9EB, self.STOP, count=100)
        require(vm.reg_read(x86.UC_X86_REG_EIP) == self.STOP, "Getter did not return")
        eax = vm.reg_read(x86.UC_X86_REG_EAX)
        require(vm.reg_read(x86.UC_X86_REG_ESP) == esp + 4, "Getter stack imbalance")
        require(vm.reg_read(x86.UC_X86_REG_EBP) == ebp, "Frame pointer changed")
        for register, value in preserved.items():
            require(vm.reg_read(register) == value, "Nonvolatile register changed")
        require(vm.mem_read(self.MANAGER + 0x4F67, 3) == bytes((0xA5, expected, 0x5A)),
                "Unexpected stored result or adjacent byte mutation")
        require(stored == expected == eax & 0xFF, "Stored/getter/expected mismatch")
        require(len(writes) == 1, "Expected exactly one remaining-days write")
        for required in (0x406AD2, 0xA75440, 0xA752F0, 0x834040, 0xB4AE30, 0x40C9EB, 0x5DB1D0):
            require(required in visited, "Missing native function " + hex(required))
        require(visited[0xA752F0] == 2, "Expected two native calendar conversions")
        return {"sample": name, "caller": caller_name, "role": role,
                "expiry_full_40": expiry, "current_full_44": current,
                "expected_days": expected, "stored_days": stored,
                "getter_al": eax & 0xFF, "getter_eax": hex(eax),
                "instructions_executed": sum(visited.values()), "manager_writes": writes,
                "native_calendar_calls": visited[0xA752F0], "passed": True}


class ApartmentLeaseClientTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not CLIENT:
            raise unittest.SkipTest("External client not supplied (--client or NANAIMO_CLIENT_EXE)")
        if uc is None:
            raise unittest.SkipTest("Optional unicorn and pefile dependencies are required")
        cls.native = NativeCalendar(CLIENT)

    def test_calendar_samples_through_native_c38e_operand_slices_and_getter(self):
        for sample in SAMPLES:
            for caller in CALLERS:
                with self.subTest(sample=sample[0], caller=caller[0]):
                    RESULTS.append(self.native.execute(sample, caller))


def main():
    global CLIENT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", help="Read-only external x86 client PE")
    parser.add_argument("--report", type=Path, help="Optional JSON evidence output")
    parser.add_argument("--disassembly", type=Path, help="Optional Capstone instruction listing")
    args = parser.parse_args()
    CLIENT = args.client or CLIENT
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(ApartmentLeaseClientTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    native = getattr(ApartmentLeaseClientTests, "native", None)
    if args.disassembly and native:
        import capstone
        decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
        lines = ["Disk PE disassembly; isolated calling context, not a live client trace."]
        for name, (start, end) in RANGES.items():
            lines.append("\n" + name)
            for ins in decoder.disasm(native.pe.get_data(start - 0x400000, end - start), start):
                lines.append(f"{ins.address:08X}  {ins.bytes.hex():24s} {ins.mnemonic} {ins.op_str}")
        args.disassembly.write_text("\n".join(lines) + "\n", encoding="utf-8")
    if args.report:
        report = {
            "evidence_level": "isolated original x86 instruction execution (Unicorn)",
            "runtime_acceptance": False,
            "limits": ["Synthetic C38E fields, manager pointer and caller stack locals",
                       "Only operand slices, calendar chain and getter executed; not complete handlers",
                       "Visitor equality does not validate owner/visitor dispatch or UI",
                       "All-zero address/time samples exercise calendar clearing, not absence-of-address dispatch",
                       "No server implementation executed; no time-zone conversion in native chain"],
            "native_stubs": [], "native_instruction_patches": [],
            "client_size": len(native.data) if native else None,
            "client_sha256": hashlib.sha256(native.data).hexdigest() if native else None,
            "unicorn_version": uc.__version__ if uc else None,
            "script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "instruction_ranges": native.spans if native else {},
            "samples_planned": len(SAMPLES) * len(CALLERS),
            "samples_passed": len(RESULTS), "results": RESULTS,
            "skipped": [reason for _, reason in result.skipped],
            "failures": [text for _, text in result.failures + result.errors],
            "passed": result.wasSuccessful() and not result.skipped,
        }
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
