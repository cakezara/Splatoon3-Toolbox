using System;
using System.IO;
using System.Linq;
using BfresLibrary;
using BfresLibrary.Helpers;
using BfresLibrary.GX2;
using Syroot.Maths;

namespace FirstPlugin
{
    public static class PaintabilityFix
    {
        public static int ApplyFix(BFRES toolboxBfres)
        {
            if (toolboxBfres == null)
                return 0;

            int fixedCount = 0;

            byte[] rawData;
            using (MemoryStream mem = new MemoryStream())
            {
                toolboxBfres.Save(mem);
                rawData = mem.ToArray();
            }

            ResFile resFile;
            using (MemoryStream mem = new MemoryStream(rawData))
            {
                resFile = new ResFile(mem);
            }

            foreach (var model in resFile.Models.Values)
            {
                foreach (var shape in model.Shapes.Values)
                {
                    var material = model.Materials[shape.MaterialIndex];

                    bool hasPaint = false;
                    foreach (var att in material.ShaderAssign.ShaderOptions)
                    {
                        if (att.Key == "blitz_paint_type" &&
                            att.Value != "<Default Value>" &&
                            att.Value != "0")
                        {
                            hasPaint = true;
                            break;
                        }
                    }

                    if (!hasPaint)
                        continue;

                    var vertexBuffer = model.VertexBuffers[shape.VertexBufferIndex];
                    VertexBufferHelper helper = new VertexBufferHelper(vertexBuffer, resFile.ByteOrder);

                    byte count = (byte)helper.Attributes.Count;
                    string[] names = { "_pu0", "_pu1", "_pu2" };

                    bool needsFix = true;
                    foreach (var att in helper.Attributes)
                    {
                        if (att.Name == names[0] || att.Name == names[1] || att.Name == names[2])
                        {
                            needsFix = false;
                            break;
                        }
                    }

                    if (!needsFix)
                        continue;

                    int stIdx = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var name = helper.Attributes[i].Name;
                        var at = helper.Attributes[i].BufferIndex;
                        if (name == "_p0" || name == "_n0" || name == "_t0" || name == "_u0" || name == "_u1" || name == "_c0")
                            stIdx = Math.Max(stIdx, at + 1);
                    }

                    foreach (var att in helper.Attributes)
                    {
                        if (att.BufferIndex >= stIdx)
                            att.BufferIndex++;
                    }

                    uint[] offsets = { 0, 12, 8 };
                    Vector4F[] pData = {
                        new Vector4F(1,1,1,1),
                        new Vector4F(1,0,0,0),
                        new Vector4F(1,1,1,-1)
                    };
                    GX2AttribFormat[] formats = {
                        GX2AttribFormat.Format_16_16_16_16_SNorm,
                        GX2AttribFormat.Format_8_SNorm,
                        GX2AttribFormat.Format_10_10_10_2_SNorm
                    };

                    for (int i = 0; i < 3; i++)
                    {
                        helper.Attributes.Add(new VertexBufferHelperAttrib());
                        helper.Attributes[count + i].Name = names[i];
                        helper.Attributes[count + i].Data = new Vector4F[helper.Attributes[0].Data.Length];
                        for (int j = 0; j < helper.Attributes[0].Data.Length; j++)
                            helper.Attributes[count + i].Data[j] = pData[i];
                        helper.Attributes[count + i].BufferIndex = (byte)stIdx;
                        helper.Attributes[count + i].Format = formats[i];
                        helper.Attributes[count + i].Offset = offsets[i];
                        helper.Attributes[count + i].Stride = 0;
                    }

                    model.VertexBuffers[shape.VertexBufferIndex] = helper.ToVertexBuffer();
                    fixedCount++;
                }
            }

            if (fixedCount == 0)
                return 0;

            byte[] fixedData;
            using (MemoryStream mem = new MemoryStream())
            {
                resFile.Save(mem);
                fixedData = mem.ToArray();
            }

            using (MemoryStream mem = new MemoryStream(fixedData))
            {
                toolboxBfres.Unload();
                toolboxBfres.Load(mem);
            }

            toolboxBfres.LoadEditors(toolboxBfres);

            return fixedCount;
        }
    }
}
