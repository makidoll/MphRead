using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MphRead
{
    public static class Read
    {
        // NOTE: When _Texture file exists, the main _Model file header will list a non-zero number of textures/palettes,
        // but the texture/palette offset will be 0 (because they're located at the start of the _Texture file).
        // However, when recolor files are used (e.g. _pal01 or flagbase_ctf_mdl -> flagbase_ctf_green_img), the number
        // of textures/palettes will be zero as well. To get the real information, the _Model file for the recolor must
        // be used in addition to the main header. And after doing that, you might then still be dealing with a _Texture file.

        public static Model GetModelByName(string name, int defaultRecolor = 0)
        {
            EntityMetadata? entityMeta = Metadata.GetEntityByName(name);
            if (entityMeta == null)
            {
                throw new ProgramException("No entity with this name is known. Please provide metadata for a custom entity.");
            }
            return GetModelByPath(entityMeta.ModelPath, defaultRecolor);
        }

        public static Model GetModelByPath(string path, int defaultRecolor = 0)
        {
            EntityMetadata? entityMeta = Metadata.GetEntityByPath(path);
            if (entityMeta == null)
            {
                throw new ProgramException("No entity at this path is known. Please provide metadata for a custom entity.");
            }
            return GetModel(entityMeta, defaultRecolor);
        }

        public static Model GetRoomByName(string name)
        {
            RoomMetadata? roomMeta = Metadata.GetRoomByName(name);
            if (roomMeta == null)
            {
                throw new ProgramException("No room with this name is known. Please provide metadata for a custom room.");
            }
            return GetRoom(roomMeta);
        }

        public static Model GetRoomById(int id)
        {
            RoomMetadata? roomMeta = Metadata.GetRoomById(id);
            if (roomMeta == null)
            {
                throw new ProgramException("No room with this ID is known.");
            }
            return GetRoom(roomMeta);
        }

        private static Model GetRoom(RoomMetadata roomMeta)
        {
            var recolors = new List<RecolorMetadata>()
            {
                new RecolorMetadata("default", roomMeta.ModelPath, roomMeta.TexturePath ?? roomMeta.ModelPath)
            };
            return GetModel(roomMeta.Name, roomMeta.ModelPath, recolors, defaultRecolor: 0);
        }

        private static Model GetModel(EntityMetadata entityMeta, int defaultRecolor)
        {
            return GetModel(entityMeta.Name, entityMeta.ModelPath, entityMeta.Recolors, defaultRecolor);
        }

        public static Model GetModelDirect(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var recolors = new List<RecolorMetadata>()
            {
                new RecolorMetadata("default", path)
            };
            return GetModel(name, path, recolors, defaultRecolor: 0);
        }

        public static Header GetHeader(string path)
        {
            path = Path.Combine(Paths.FileSystem, path);
            ReadOnlySpan<byte> bytes = ReadBytes(path);
            return ReadStruct<Header>(bytes[0..Sizes.Header]);
        }

        private static Model GetModel(string name, string modelPath, IReadOnlyList<RecolorMetadata> recolorMeta, int defaultRecolor)
        {
            if (defaultRecolor < 0 || defaultRecolor > recolorMeta.Count)
            {
                throw new ProgramException("The specified recolor index is invalid for this entity.");
            }
            string path = Path.Combine(Paths.FileSystem, modelPath);
            ReadOnlySpan<byte> initialBytes = ReadBytes(path);
            Header header = ReadStruct<Header>(initialBytes[0..Sizes.Header]);
            IReadOnlyList<RawNode> nodes = DoOffsets<RawNode>(initialBytes, header.NodeOffset, header.NodeCount);
            IReadOnlyList<Mesh> meshes = DoOffsets<Mesh>(initialBytes, header.MeshOffset, header.MeshCount);
            IReadOnlyList<DisplayList> dlists = DoOffsets<DisplayList>(initialBytes, header.DlistOffset, header.MeshCount);
            var instructions = new List<IReadOnlyList<RenderInstruction>>();
            foreach (DisplayList dlist in dlists)
            {
                instructions.Add(DoRenderInstructions(initialBytes, dlist));
            }
            IReadOnlyList<RawMaterial> materials = DoOffsets<RawMaterial>(initialBytes, header.MaterialOffset, header.MaterialCount);
            var recolors = new List<Recolor>();
            foreach (RecolorMetadata meta in recolorMeta)
            {
                ReadOnlySpan<byte> modelBytes = initialBytes;
                Header modelHeader = header;
                // todo: this check is causing a double load (full path vs. relative)
                if (meta.ModelPath != path)
                {
                    modelBytes = ReadBytes(meta.ModelPath);
                    modelHeader = ReadStruct<Header>(modelBytes[0..Sizes.Header]);
                }
                IReadOnlyList<Texture> textures = DoOffsets<Texture>(modelBytes, modelHeader.TextureOffset, modelHeader.TextureCount);
                IReadOnlyList<Palette> palettes = DoOffsets<Palette>(modelBytes, modelHeader.PaletteOffset, modelHeader.PaletteCount);
                ReadOnlySpan<byte> textureBytes = modelBytes;
                if (meta.TexturePath != meta.ModelPath)
                {
                    textureBytes = ReadBytes(meta.TexturePath);
                }
                ReadOnlySpan<byte> paletteBytes = textureBytes;
                if (meta.PalettePath != meta.TexturePath)
                {
                    paletteBytes = ReadBytes(meta.PalettePath);
                    if (meta.SeparatePaletteHeader)
                    {
                        Header paletteHeader = ReadStruct<Header>(paletteBytes[0..Sizes.Header]);
                        palettes = DoOffsets<Palette>(paletteBytes, paletteHeader.PaletteOffset, paletteHeader.PaletteCount);
                    }
                }
                var textureData = new List<IReadOnlyList<TextureData>>();
                var paletteData = new List<IReadOnlyList<PaletteData>>();
                foreach (Texture texture in textures)
                {
                    textureData.Add(GetTextureData(texture, textureBytes));
                }
                foreach (Palette palette in palettes)
                {
                    paletteData.Add(GetPaletteData(palette, paletteBytes));
                }
                recolors.Add(new Recolor(meta.Name, textures, palettes, textureData, paletteData));
            }
            return new Model(name, header, nodes, meshes, materials, dlists, instructions, recolors, defaultRecolor);
        }

        private static ReadOnlySpan<byte> ReadBytes(string path)
        {
            return new ReadOnlySpan<byte>(File.ReadAllBytes(Path.Combine(Paths.FileSystem, path)));
        }

        private static IReadOnlyList<TextureData> GetTextureData(Texture texture, ReadOnlySpan<byte> textureBytes)
        {
            var data = new List<TextureData>();
            int pixelCount = texture.Width * texture.Height;
            int entriesPerByte = 1;
            if (texture.Format == TextureFormat.Palette2Bit)
            {
                entriesPerByte = 4;
            }
            else if (texture.Format == TextureFormat.Palette4Bit)
            {
                entriesPerByte = 2;
            }
            if (pixelCount % entriesPerByte != 0)
            {
                throw new ProgramException($"Pixel count {pixelCount} is not divisible by {entriesPerByte}.");
            }
            pixelCount /= entriesPerByte;
            if (texture.Format == TextureFormat.DirectRgb || texture.Format == TextureFormat.DirectRgba)
            {
                for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    ushort color = SpanReadUshort(textureBytes, (int)(texture.ImageOffset + pixelIndex * 2));
                    byte alpha = texture.Format == TextureFormat.DirectRgb ? (byte)255 : AlphaFromShort(color);
                    data.Add(new TextureData(color, alpha));
                }
            }
            else
            {
                for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    byte entry = textureBytes[(int)(texture.ImageOffset + pixelIndex)];
                    for (int entryIndex = 0; entryIndex < entriesPerByte; entryIndex++)
                    {
                        uint index = (uint)(entry >> ((pixelIndex * entriesPerByte + entryIndex) % entriesPerByte
                            * (8 / entriesPerByte)));
                        byte alpha = 255;
                        if (texture.Format == TextureFormat.Palette2Bit)
                        {
                            index &= 0x3;
                        }
                        else if (texture.Format == TextureFormat.Palette4Bit)
                        {
                            index &= 0xF;
                        }
                        else if (texture.Format == TextureFormat.PaletteA5I3)
                        {
                            index &= 0x7;
                            alpha = AlphaFromA5I3(entry);
                        }
                        else if (texture.Format == TextureFormat.PaletteA3I5)
                        {
                            index &= 0x1F;
                            alpha = AlphaFromA3I5(entry);
                        }
                        if (texture.Format == TextureFormat.Palette2Bit || texture.Format == TextureFormat.Palette4Bit
                            || texture.Format == TextureFormat.Palette8Bit)
                        {
                            if (texture.Opaque == 0 && index == 0)
                            {
                                alpha = 0;
                            }
                        }
                        data.Add(new TextureData(index, alpha));
                    }
                }
            }
            return data;
        }

        private static IReadOnlyList<PaletteData> GetPaletteData(Palette palette, ReadOnlySpan<byte> paletteBytes)
        {
            if (palette.Count % 2 != 0)
            {
                throw new ProgramException($"Palette count {palette.Count} is not divisible by 2.");
            }
            var data = new List<PaletteData>();
            for (int i = 0; i < palette.Count / 2; i++)
            {
                ushort entry = SpanReadUshort(paletteBytes, (int)(palette.Offset + i * 2));
                data.Add(new PaletteData(entry));
            }
            return data;
        }

        private static ColorRgba ColorFromShort(ushort value, byte alpha)
        {
            byte red = (byte)(((value >> 0) & 0x1F) << 3);
            byte green = (byte)(((value >> 5) & 0x1F) << 3);
            byte blue = (byte)(((value >> 10) & 0x1F) << 3);
            return new ColorRgba(red, green, blue, alpha);
        }

        private static byte AlphaFromShort(ushort value) => (value & 0x8000) == 0 ? (byte)255 : (byte)0;

        private static byte AlphaFromA5I3(byte value) => (byte)((value >> 3) / 31.0f * 255.0f);

        private static byte AlphaFromA3I5(byte value) => (byte)((value >> 5) / 7.0f * 255.0f);

        public static IReadOnlyList<Entity> GetEntities(string path, int layerId)
        {
            path = Path.Combine(Paths.FileSystem, path);
            ReadOnlySpan<byte> bytes = ReadBytes(path);
            EntityHeader header = ReadStruct<EntityHeader>(bytes[0..Sizes.EntityHeader]);
            if (header.Version != 2)
            {
                throw new ProgramException($"Unexpected entity header version {header.Version}.");
            }
            var entities = new List<Entity>();
            for (int i = 0; entities.Count < header.Lengths[layerId]; i++)
            {
                int start = Sizes.EntityHeader + Sizes.EntityEntry * i;
                int end = start + Sizes.EntityEntry;
                EntityEntry entry = ReadStruct<EntityEntry>(bytes[start..end]);
                if ((entry.LayerMask & (1 << layerId)) != 0)
                {
                    start = (int)entry.DataOffset;
                    end = start + Sizes.EntityDataHeader;
                    EntityDataHeader init = ReadStruct<EntityDataHeader>(bytes[start..end]);
                    var type = (EntityType)init.Type;
                    end = start + entry.Length;
                    // todo: handle more entity types
                    if (type == EntityType.JumpPad)
                    {
                        Debug.Assert(entry.Length == Sizes.JumpPadEntityData);
                        JumpPadEntityData data = ReadStruct<JumpPadEntityData>(bytes[start..end]);
                        entities.Add(new Entity<JumpPadEntityData>(entry, type, init.SomeId, data));
                    }
                    else if (type == EntityType.Item || type == EntityType.Pickup)
                    {
                        Debug.Assert(entry.Length == Sizes.ItemEntityData);
                        ItemEntityData data = ReadStruct<ItemEntityData>(bytes[start..end]);
                        entities.Add(new Entity<ItemEntityData>(entry, type, init.SomeId, data));
                    }
                    else
                    {
                        entities.Add(new Entity(entry, type, init.SomeId));
                    }
                }
            }
            return entities;
        }
        
        private static void Nop() { }

        private static IReadOnlyList<RenderInstruction> DoRenderInstructions(ReadOnlySpan<byte> bytes, DisplayList dlist)
        {
            if (dlist.Size % 4 != 0)
            {
                throw new ProgramException($"Dlist size {dlist.Size} not divisible by 4.");
            }
            var list = new List<RenderInstruction>();
            int pointer = (int)dlist.Offset;
            int endPointer = pointer + (int)dlist.Size;
            if (endPointer >= bytes.Length)
            {
                throw new ProgramException($"End pointer size {endPointer} too long for dlist size {dlist.Size}.");
            }
            while (pointer < endPointer)
            {
                uint packedInstructions = SpanReadUint(bytes, ref pointer);
                for (int i = 0; i < 4; i++)
                {
                    var instruction = (InstructionCode)(((packedInstructions & 0xFF) << 2) + 0x400);
                    int arity = RenderInstruction.GetArity(instruction);
                    var arguments = new List<uint>();
                    for (int j = 0; j < arity; j++)
                    {
                        arguments.Add(SpanReadUint(bytes, ref pointer));
                    }
                    list.Add(new RenderInstruction(instruction, arguments.ToArray()));
                    packedInstructions >>= 8;
                }
            }
            return list;
        }

        private static uint SpanReadUint(ReadOnlySpan<byte> bytes, ref int offset)
        {
            uint result = MemoryMarshal.Read<uint>(bytes[offset..(offset + sizeof(uint))]);
            offset += sizeof(uint);
            return result;
        }

        private static ushort SpanReadUshort(ReadOnlySpan<byte> bytes, ref int offset)
        {
            ushort result = MemoryMarshal.Read<ushort>(bytes[offset..(offset + sizeof(ushort))]);
            offset += sizeof(ushort);
            return result;
        }

        private static ushort SpanReadUshort(ReadOnlySpan<byte> bytes, int offset)
        {
            return SpanReadUshort(bytes, ref offset);
        }

        private static string GetModelName(string path)
        {
            if (path.Contains("_mdl_"))
            {
                path = path.Replace("_mdl_", "_");
            }
            if (path.Contains("_Model.bin"))
            {
                path = path.Replace("_Model.bin", "");
            }
            else if (path.Contains("_model.bin"))
            {
                path = path.Replace("_model.bin", "");
            }
            return Path.GetFileNameWithoutExtension(path);
        }

        private static IReadOnlyList<T> DoOffsets<T>(ReadOnlySpan<byte> bytes, uint offset, int count) where T : struct
        {
            int ioffset = (int)offset;
            var results = new List<T>();
            if (offset != 0x0)
            {
                int size = Marshal.SizeOf(typeof(T));
                for (uint i = 0; i < count; i++, ioffset += size)
                {
                    results.Add(ReadStruct<T>(bytes[ioffset..(ioffset + size)]));
                }
            }
            return results;
        }

        private static T ReadStruct<T>(ReadOnlySpan<byte> bytes) where T : struct
        {
            var handle = GCHandle.Alloc(bytes.ToArray(), GCHandleType.Pinned);
            object? result = Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T));
            handle.Free();
            if (result == null)
            {
                throw new ProgramException($"Failed to read {typeof(T)} struct.");
            }
            return (T)result;
        }
    }
}