from __future__ import annotations

import struct


def discover_eye_probe(data: bytes) -> dict[str, object]:
    def require(condition: bool, reason: str) -> None:
        if not condition:
            raise ValueError(reason)

    def unpack(fmt: str, offset: int) -> tuple[int, ...]:
        require(0 <= offset <= len(data) - struct.calcsize(fmt), "Truncated engine")
        return struct.unpack_from(fmt, data, offset)

    require(data[:7] == b"\x7fELF\x02\x01\x01", "Expected little-endian ELF64")
    require(unpack("<HHI", 16) == (3, 183, 1), "Expected an AArch64 shared library")
    phoff, = unpack("<Q", 32)
    phsize, count = unpack("<HH", 54)
    require(phsize == 56 and 0 < count < 256, "Unsupported ELF program headers")
    segments = []
    for index in range(count):
        kind, flags, offset, address, _, size, memory_size, _ = unpack("<IIQQQQQQ", phoff + index * phsize)
        if kind == 1:
            require(size <= memory_size and offset + size <= len(data), "Truncated ELF segment")
            segments.append((offset, address, size, flags))

    def translate(value: int, *, to_file: bool, length: int = 1, flags: int = 0) -> int:
        matches = []
        for offset, address, size, permissions in segments:
            source, target = (address, offset) if to_file else (offset, address)
            if permissions & flags == flags and source <= value and value + length <= source + size:
                matches.append(target + value - source)
        require(len(matches) == 1, "Address is not in a unique file-backed ELF segment")
        return matches[0]

    def matches(needle: bytes, *, writable: bool = False) -> list[int]:
        found = []
        for offset, _, size, flags in segments:
            if writable and not flags & 2:
                continue
            cursor = offset
            while (cursor := data.find(needle, cursor, offset + size)) >= 0:
                if not writable or cursor % 8 == 0:
                    found.append(cursor)
                cursor += 1
        return found

    def unique(items: list[int], description: str) -> int:
        require(len(items) == 1, f"Missing or ambiguous {description}")
        return items[0]

    name = unique(matches(b"N12eye_tracking18VisualAxisDetectorE\0"), "VisualAxisDetector RTTI")
    name_address = translate(name, to_file=False)
    name_pointer = unique(matches(struct.pack("<Q", name_address), writable=True), "detector typeinfo")
    typeinfo = translate(name_pointer - 8, to_file=False, length=24, flags=2)
    vtable = unique([
        pointer - 8 for pointer in matches(struct.pack("<Q", typeinfo), writable=True)
        if pointer >= 8 and unpack("<Q", pointer - 8) == (0,)
    ], "detector vtable")
    translate(vtable, to_file=False, length=40, flags=2)
    process_address, = unpack("<Q", vtable + 32)
    process = translate(process_address, to_file=True, length=4, flags=1)

    def function_words(offset: int, limit: int) -> list[int]:
        require(offset % 4 == 0, "Unaligned instruction address")
        words = []
        for cursor in range(offset, offset + limit, 4):
            translate(cursor, to_file=False, length=4, flags=1)
            word, = unpack("<I", cursor)
            words.append(word)
            if word == 0xD65F03C0:
                return words
        raise ValueError("Unsupported function boundary")

    process_words = function_words(process, 0x400)
    require(process_words[:4] == [0xA9BD7BFD, 0xA90157F6, 0xA9024FF4, 0x910003FD],
            "Unsupported detector process prologue")
    immediate_mask = 0xFFC003FF
    require(process_words[4] & immediate_mask == 0xF9400034
            and process_words[5] == 0xAA0003F3
            and process_words[6] & immediate_mask == 0x39400288
            and process_words[7] == 0x7100051F
            and process_words[8] & 0xFF00001F == 0x54000001,
            "Unsupported detector EyeData access")
    input_valid = (process_words[6] >> 10) & 0xFFF
    calls = []
    for index in range(2, len(process_words)):
        word = process_words[index]
        if process_words[index - 2:index] == [0xAA1303E0, 0xAA1403E1] and word >> 26 == 0x25:
            displacement = word & 0x03FFFFFF
            if displacement & 0x02000000:
                displacement -= 0x04000000
            target = process_address + index * 4 + displacement * 4
            calls.append((process + index * 4 + 4, target))
    require(len(calls) == 2 and calls[0][1] == calls[1][1], "Unsupported detector helper calls")
    helper = translate(calls[0][1], to_file=True, length=4, flags=1)
    words = function_words(helper, 0x1000)
    require(0xAA0103F3 in words[:32], "Detector helper does not retain EyeData in x19")
    require(0x39400028 | input_valid << 10 in words[:32], "Detector helper input layout differs")

    candidates = []
    for index in range(len(words) - 5):
        if words[index] & immediate_mask != 0xFD000260 or words[index + 1] & immediate_mask != 0xBD000261:
            continue
        axis = ((words[index] >> 10) & 0xFFF) * 8
        z = ((words[index + 1] >> 10) & 0xFFF) * 4
        if not (0x100 <= axis < 0x1000 and z == axis + 8 and input_valid == axis + 0x5C):
            continue
        if words[index + 2:index + 5] != [0xF9401688, 0xF85D83A9, 0xEB09011F]:
            continue
        before = words[:index]
        if (0xFD400260 | ((axis + 0x50) // 8) << 10 not in before
                or 0xBD400261 | ((axis + 0x58) // 4) << 10 not in before
                or 0x39000268 | (axis + 12) << 10 not in before):
            continue
        candidates.append(axis)
    axis = unique(candidates, "detector vector write")
    return {
        "event": "detector_output",
        "offset": calls[0][0],
        "extra_offsets": [calls[1][0]],
        "fetch": (f"x=+0x{axis:x}(%x20):x32 y=+0x{axis + 4:x}(%x20):x32 "
                  f"z=+0x{axis + 8:x}(%x20):x32 tag=+0x0(%x20):x8 "
                  f"valid=+0x{axis + 12:x}(%x20):x8"),
        "axis_offset": axis,
    }
