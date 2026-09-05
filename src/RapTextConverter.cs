using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DayZModWorkbench
{
    // Leitor limitado e validado do formato raP usado por configs e materiais.
    // Diferente do CfgConvert, qualquer offset, tipo ou contagem inválida encerra
    // a conversão em vez de deixar o processo preso em um arquivo defeituoso.
    internal static class RapTextConverter
    {
        private const int HeaderSize = 16;
        private const int MaximumDepth = 128;

        public static bool TryConvert(string sourcePath, string destinationPath, out string error)
        {
            error = string.Empty;
            try
            {
                byte[] data = File.ReadAllBytes(sourcePath);
                Parser parser = new Parser(data);
                string text = parser.Convert();
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidDataException("o documento RaP reconstruído ficou vazio");
                File.WriteAllText(destinationPath, text, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
                return false;
            }
        }

        private sealed class Parser
        {
            private readonly byte[] _data;
            private readonly int _dataEnd;
            private readonly HashSet<int> _activeBodies = new HashSet<int>();
            private int _position;

            internal Parser(byte[] data)
            {
                _data = data ?? throw new ArgumentNullException("data");
                if (data.Length < HeaderSize || data[0] != 0 || data[1] != 0x72 ||
                    data[2] != 0x61 || data[3] != 0x50)
                    throw new InvalidDataException("assinatura RaP ausente");
                if (ReadUInt32At(4) != 0 || ReadUInt32At(8) != 8)
                    throw new InvalidDataException("cabeçalho RaP não suportado");
                int replacementSequences = 0;
                for (int i = HeaderSize; i < data.Length - 2; i++)
                {
                    if (data[i] == 0xef && data[i + 1] == 0xbf && data[i + 2] == 0xbd)
                    {
                        replacementSequences++;
                        i += 2;
                    }
                }
                if (replacementSequences > 0)
                    throw new InvalidDataException("foram detectadas " + replacementSequences +
                        " sequência(s) UTF-8 EF BF BD: bytes do RaP original já foram substituídos dentro do PBO");
                uint enumOffset = ReadUInt32At(12);
                _dataEnd = enumOffset == 0 ? data.Length : checked((int)enumOffset);
                if (_dataEnd < HeaderSize || _dataEnd > data.Length)
                    throw new InvalidDataException("offset da tabela de enums fora do arquivo");
            }

            internal string Convert()
            {
                StringBuilder output = new StringBuilder();
                WriteBody(HeaderSize, output, 0, false, null);
                return output.ToString();
            }

            private void WriteBody(int offset, StringBuilder output, int depth, bool namedClass, string className)
            {
                if (depth > MaximumDepth) throw new InvalidDataException("profundidade de classes excedida");
                if (offset < HeaderSize || offset >= _dataEnd)
                    throw new InvalidDataException("offset de classe fora da área de dados: " + offset);
                if (!_activeBodies.Add(offset)) throw new InvalidDataException("ciclo de classes detectado");

                int returnPosition = _position;
                try
                {
                    _position = offset;
                    string inherited = ReadCString();
                    int entryCount = ReadCompressedInteger();
                    if (entryCount < 0 || entryCount > _dataEnd - _position)
                        throw new InvalidDataException("quantidade inválida de entradas na classe");

                    if (namedClass)
                    {
                        Indent(output, depth - 1);
                        output.Append("class ").Append(className);
                        if (!string.IsNullOrEmpty(inherited)) output.Append(": ").Append(inherited);
                        output.AppendLine();
                        Indent(output, depth - 1);
                        output.AppendLine("{");
                    }
                    else if (!string.IsNullOrEmpty(inherited))
                    {
                        throw new InvalidDataException("a classe raiz possui herança inesperada");
                    }

                    for (int i = 0; i < entryCount; i++) WriteEntry(output, depth);

                    if (namedClass)
                    {
                        Indent(output, depth - 1);
                        output.AppendLine("};");
                    }
                }
                finally
                {
                    _activeBodies.Remove(offset);
                    _position = returnPosition;
                }
            }

            private void WriteEntry(StringBuilder output, int depth)
            {
                byte type = ReadByte();
                switch (type)
                {
                    case 0:
                    {
                        string name = ReadIdentifier("classe");
                        int bodyOffset = checked((int)ReadUInt32());
                        WriteBody(bodyOffset, output, depth + 1, true, name);
                        break;
                    }
                    case 1:
                    {
                        byte subtype = ReadByte();
                        string name = ReadIdentifier("propriedade");
                        Indent(output, depth);
                        output.Append(name).Append('=').Append(ReadValue(subtype)).AppendLine(";");
                        break;
                    }
                    case 2:
                    {
                        string name = ReadIdentifier("array");
                        Indent(output, depth);
                        output.Append(name).Append("[]={");
                        WriteArray(output, depth);
                        output.AppendLine("};");
                        break;
                    }
                    case 3:
                        Indent(output, depth);
                        output.Append("class ").Append(ReadIdentifier("classe externa")).AppendLine(";");
                        break;
                    case 4:
                        Indent(output, depth);
                        output.Append("delete ").Append(ReadIdentifier("classe excluída")).AppendLine(";");
                        break;
                    case 5:
                    {
                        string name = ReadIdentifier("array incremental");
                        uint flags = ReadUInt32();
                        Indent(output, depth);
                        output.Append(name).Append((flags & 1) != 0 ? "[]+={" : "[]={");
                        WriteArray(output, depth);
                        output.AppendLine("};");
                        break;
                    }
                    default:
                        throw new InvalidDataException("tipo de entrada RaP desconhecido: " + type);
                }
            }

            private void WriteArray(StringBuilder output, int depth)
            {
                int count = ReadCompressedInteger();
                if (count < 0 || count > _dataEnd - _position)
                    throw new InvalidDataException("quantidade inválida de elementos no array");
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) output.Append(',');
                    byte subtype = ReadByte();
                    if (subtype == 3)
                    {
                        output.Append('{');
                        WriteArray(output, depth + 1);
                        output.Append('}');
                    }
                    else
                    {
                        output.Append(ReadValue(subtype));
                    }
                }
            }

            private string ReadValue(byte subtype)
            {
                switch (subtype)
                {
                    case 0:
                        return Quote(ReadCString());
                    case 1:
                    {
                        float value = ReadSingle();
                        if (float.IsNaN(value) || float.IsInfinity(value))
                            throw new InvalidDataException("valor decimal inválido");
                        string text = value.ToString("R", CultureInfo.InvariantCulture);
                        if (text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && text.IndexOf('e') < 0) text += ".0";
                        return text;
                    }
                    case 2:
                        return ReadInt32().ToString(CultureInfo.InvariantCulture);
                    case 4:
                        return ReadCString();
                    case 6:
                        // BI/DayZ RaP subtype 6 stores a signed 64-bit integer.
                        // Large values (for example very high hitpoints in some mods)
                        // cannot be represented as subtype 2 / Int32.
                        return ReadInt64().ToString(CultureInfo.InvariantCulture);
                    default:
                        throw new InvalidDataException("subtipo de valor RaP desconhecido: " + subtype);
                }
            }

            private string ReadIdentifier(string description)
            {
                string value = ReadCString();
                if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '\r', '\n', '{', '}', ';', '=' }) >= 0)
                    throw new InvalidDataException("nome inválido de " + description);
                return value;
            }

            private string ReadCString()
            {
                int start = _position;
                while (_position < _dataEnd && _data[_position] != 0) _position++;
                if (_position >= _dataEnd) throw new EndOfStreamException("string RaP sem terminador");
                string value = Encoding.UTF8.GetString(_data, start, _position - start);
                _position++;
                return value;
            }

            private int ReadCompressedInteger()
            {
                int value = 0;
                int shift = 0;
                for (int i = 0; i < 5; i++)
                {
                    byte current = ReadByte();
                    value |= (current & 0x7f) << shift;
                    if ((current & 0x80) == 0) return value;
                    shift += 7;
                }
                throw new InvalidDataException("inteiro comprimido RaP inválido");
            }

            private byte ReadByte()
            {
                Require(1);
                return _data[_position++];
            }

            private int ReadInt32()
            {
                Require(4);
                int value = BitConverter.ToInt32(_data, _position);
                _position += 4;
                return value;
            }

            private long ReadInt64()
            {
                Require(8);
                long value = BitConverter.ToInt64(_data, _position);
                _position += 8;
                return value;
            }

            private uint ReadUInt32()
            {
                Require(4);
                uint value = BitConverter.ToUInt32(_data, _position);
                _position += 4;
                return value;
            }

            private float ReadSingle()
            {
                Require(4);
                float value = BitConverter.ToSingle(_data, _position);
                _position += 4;
                return value;
            }

            private uint ReadUInt32At(int offset)
            {
                if (offset < 0 || offset + 4 > _data.Length) throw new EndOfStreamException();
                return BitConverter.ToUInt32(_data, offset);
            }

            private void Require(int count)
            {
                if (count < 0 || _position < HeaderSize || _position > _dataEnd - count)
                    throw new EndOfStreamException("estrutura RaP termina antes do esperado");
            }

            private static string Quote(string value)
            {
                return "\"" + value.Replace("\"", "\"\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
            }

            private static void Indent(StringBuilder output, int depth)
            {
                output.Append(' ', Math.Max(0, depth) * 4);
            }
        }
    }
}
