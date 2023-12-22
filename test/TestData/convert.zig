const std = @import("std");
const c = @cImport({
    @cInclude("ogg/ogg.h");
    @cInclude("vorbis/codec.h");
});

pub fn main() !void {
    var allocator = std.heap.c_allocator;

    const dir = try std.fs.cwd().openIterableDir(".", .{});

    var iterator = dir.iterate();
    while (try iterator.next()) |entry| {
        if (entry.kind == .file and std.mem.eql(u8, "ogg", std.fs.path.extension(entry.name))) {
            try processOgg(try std.fs.cwd().openFile(entry.name, .{}), allocator);
        }
    }
}

const OggErrors = error{ EndOfFile, InternalOggError };

pub fn processOgg(f: std.fs.File, allocator: std.mem.Allocator) !void {
    var sync: c.ogg_sync_state = undefined;
    _ = c.ogg_sync_init(&sync);
    _ = try readPage(&sync, f, allocator);
}

pub fn readPage(s: *c.ogg_sync_state, f: std.fs.File, allocator: std.mem.Allocator) !c.ogg_page {
    _ = allocator;
    var ret: c.ogg_page = undefined;

    while (c.ogg_sync_pageout(s, &ret) != 1) {
        var buffer = c.ogg_sync_buffer(s, 8192);
        var count = try f.read(buffer[0..8192]);

        if (count == 0) return OggErrors.EndOfFile;

        _ = c.ogg_sync_wrote(s, @intCast(count));
    }

    // for some reason the ogg_sync_check symbol is missing when i try to link so just transcluding the implementation here
    if (s.storage < 0)
        return OggErrors.InternalOggError;

    return ret;
}

const OV = enum(c_int) {
    OK = 0,
    FALSE = c.OV_FALSE,
    EOF = c.OV_EOF,
    HOLE = c.OV_HOLE,

    EREAD = c.OV_EREAD,
    EFAULT = c.OV_EFAULT,
    EIMPL = c.OV_EIMPL,
    EINVAL = c.OV_EINVAL,
    ENOTVORBIS = c.OV_ENOTVORBIS,
    EBADHEADER = c.OV_EBADHEADER,
    EVERSION = c.OV_EVERSION,
    ENOTAUDIO = c.OV_ENOTAUDIO,
    EBADPACKET = c.OV_EBADPACKET,
    EBADLINK = c.OV_EBADLINK,
    ENOSEEK = c.OV_ENOSEEK,
};

const PageInfo = struct {
    bos: bool,
    eos: bool,
    granulePos: i64,
    serialNum: i32,
    pageNum: i32,
    continued: bool,
    packets: i32,
    body: []u8,
};
