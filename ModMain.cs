using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using Steamworks;
using UnityEngine;
using GeneszSteamP2PBase; 

namespace TheLongDriveRadioSync
{
    public class ModMain : MelonMod
    {
        private static List<string> _audioFiles = new List<string>();
        public static List<AudioFilePacket> AudioFilesData = new List<AudioFilePacket>();

        public const byte ModPacketID = 245; 

        private const int TargetSceneIndex = 1;

        public override void OnInitializeMelon()
        {
            var harmony = new HarmonyLib.Harmony("tld.DeltaNeverUsed.SyncRadio");
            harmony.PatchAll();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            MelonLogger.Msg($"Scene {buildIndex} loaded: {sceneName}");
            if (buildIndex == TargetSceneIndex)
            {
                checkMedia = true;
            }
        }

        private bool checkMedia;

        public override void OnFixedUpdate()
        {
            if (!SteamP2PNetworkingUnderTheHoodScript.IsSteam() || settingsscript.s == null || !checkMedia)
                return;

            checkMedia = false;
            _audioFiles.Clear();
            AudioFilesData.Clear();

            var customRadioPath = settingsscript.s.S.SCustomRadioPath;
            MelonLogger.Msg("Scanning custom radio folder: " + customRadioPath);

            try
            {
                if (Directory.Exists(customRadioPath))
                {
                    foreach (string file in Directory.GetFiles(customRadioPath))
                    {
                        AddFile(file);
                    }

                    foreach (string directory in Directory.GetDirectories(customRadioPath, "*", SearchOption.AllDirectories))
                    {
                        foreach (string file in Directory.GetFiles(directory))
                        {
                           AddFile(file);
                        }
                    }
                }

                foreach (var path in _audioFiles)
                {
                    try 
                    {
                        var readData = File.ReadAllBytes(path);
                        if (readData.Length > 33554432)
                        {
                            MelonLogger.Warning($"{Path.GetFileName(path)} too big, skipping sync.");
                            continue;
                        }

                        var a = new AudioFilePacket
                        {
                            fileName = Path.GetFileName(path),
                            data = readData
                        };
                        AudioFilesData.Add(a);
                    }
                    catch (Exception ex)
                    {
                         MelonLogger.Error($"Failed to read file {path}: {ex.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("Error scanning custom radio folder: " + e.Message);
            }
        }

        private void AddFile(string file)
        {
            if (!_audioFiles.Contains(file))
            {
                _audioFiles.Add(file);
                string format = Path.GetExtension(file).TrimStart('.').ToUpper();
                MelonLogger.Msg($"Found file: {Path.GetFileName(file)} (Format: {format})");
            }
        }
    }

    [Serializable]
    public struct RadioPacket
    {
        public string fileName;
    }

    [Serializable]
    public struct AudioFilePacket
    {
        public string fileName;
        public byte[] data;
    }

    [Serializable]
    public struct PacketPart
    {
        public uint size;
        public uint pId;
        public uint pIndex;
        public byte[] data;
    }

    [Serializable]
    public struct AudioFileRequestPacket
    {
        public string[] excludes;
    }

    public static class SendP2P
    {
        public static void Send<T>(CSteamID _id, T obj)
        {
            var data = new[] { ModMain.ModPacketID }.Concat(NetworkHelper.ObjectToByteArray(obj)).ToArray();
            SteamP2PNetworkingUnderTheHoodScript.SendBytesToTarget(_id, data, EP2PSend.k_EP2PSendReliable);
        }
    }

    [HarmonyPatch(typeof(networkingScript), "ProcessMessage")]
    class Patch
    {
        private static System.Random _rand = new System.Random();
        
        public static List<CSteamID> AlreadySend = new List<CSteamID>();
        private static Dictionary<uint, List<PacketPart>> _recv = new Dictionary<uint, List<PacketPart>>();

        private static void SendFile(CSteamID _id, AudioFileRequestPacket o)
        {
            foreach (var audioFile in ModMain.AudioFilesData)
            {
                if (o.excludes != null && o.excludes.Contains(audioFile.fileName))
                {
                    continue;
                }

                MelonLogger.Msg($"Sending \"{audioFile.fileName}\" to {_id.m_SteamID}");
                var fullAudioPacket = NetworkHelper.ObjectToByteArray(audioFile);
                var chunkSize = 400 * 1024;
                var numChunks = (int)Math.Ceiling((double)fullAudioPacket.Length / chunkSize);

                var packetId = (uint)_rand.Next();

                for (uint i = 0; i < numChunks; i++)
                {
                    int offset = (int)i * chunkSize;
                    int size = Math.Min(chunkSize, fullAudioPacket.Length - offset);
                    var data = new byte[size];
                    Array.Copy(fullAudioPacket, offset, data, 0, size);

                    var packet = new PacketPart
                    {
                        size = (uint)numChunks,
                        pId = packetId,
                        pIndex = i,
                        data = data
                    };
                    Thread.Sleep(250);
                    SendP2P.Send(_id, packet);
                }
                Thread.Sleep(1000);
            }
        }

        private static bool Prefix(CSteamID _SenderID, byte[] _bytes)
        {
            if (_bytes == null || _bytes.Length == 0 || _bytes[0] != ModMain.ModPacketID)
                return true;

            try
            {
                var obj = NetworkHelper.ByteArrayToObject(_bytes.Skip(1).ToArray());
                var objType = obj.GetType();

                if (objType == typeof(AudioFileRequestPacket))
                {
                    if (AlreadySend.Contains(_SenderID)) return false;
                    AlreadySend.Add(_SenderID);

                    var o = (AudioFileRequestPacket)obj;
                    var t = new Thread(() => SendFile(_SenderID, o));
                    t.Start();
                }

                if (objType == typeof(RadioPacket))
                {
                    var o = (RadioPacket)obj;
                    MelonLogger.Msg("Host says play: " + o.fileName);
                    
                    string fullPath = settingsscript.s.S.SCustomRadioPath + "/" + o.fileName;
                    // ИСПРАВЛЕНИЕ: mainscript.M заменено на mainscript.s
                    if(mainscript.s != null && mainscript.s.customRadio != null)
                        mainscript.s.customRadio.StartCoroutine(mainscript.s.customRadio.LoadOneSong(fullPath));
                }

                if (objType == typeof(PacketPart))
                {
                    var o = (PacketPart)obj;
                    if (!_recv.ContainsKey(o.pId))
                    {
                        if (o.pIndex > 20 && o.size > 200) return false; 
                        _recv.Add(o.pId, new List<PacketPart>());
                    }

                    if (!_recv[o.pId].Any(x => x.pIndex == o.pIndex))
                    {
                         _recv[o.pId].Add(o);
                    }

                    if (_recv[o.pId].Count >= o.size)
                    {
                        MelonLogger.Msg("File download complete. Recombining...");
                        var fullPacketParts = _recv[o.pId].OrderBy(p => p.pIndex).ToArray();
                        
                        using (var ms = new MemoryStream())
                        {
                            foreach (var part in fullPacketParts)
                            {
                                ms.Write(part.data, 0, part.data.Length);
                            }
                            
                            byte[] recombinedData = ms.ToArray();
                            _recv.Remove(o.pId);

                            try
                            {
                                var innerObj = NetworkHelper.ByteArrayToObject(recombinedData);
                                if (innerObj is AudioFilePacket filePacket)
                                {
                                    string savePath = settingsscript.s.S.SCustomRadioPath + "/" + filePacket.fileName;
                                    MelonLogger.Msg($"Writing file to disk: {filePacket.fileName} ({filePacket.data.Length} bytes)");
                                    File.WriteAllBytes(savePath, filePacket.data);
                                    
                                    var newPacket = new AudioFilePacket { fileName = filePacket.fileName, data = null };
                                    ModMain.AudioFilesData.Add(newPacket);
                                }
                            }
                            catch (Exception e)
                            {
                                MelonLogger.Error("Recombination error: " + e);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Packet Error: {ex}");
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(networkingScript), "ClientSendOnJoin")]
    class PatchStart
    {
        private static void Postfix()
        {
            Patch.AlreadySend.Clear();
            if (SteamP2PNetworkingUnderTheHoodScript.IsHost())
                return;

            var requestFiles = new AudioFileRequestPacket
            {
                excludes = ModMain.AudioFilesData.Select(a => a.fileName).ToArray()
            };

            MelonLogger.Msg("Joined lobby. Requesting songs from Host...");
            
            CSteamID hostId = SteamMatchmaking.GetLobbyOwner(networkingScript.s.hood.ConnectedLobbyID);
            SendP2P.Send(hostId, requestFiles);
        }
    }

    [HarmonyPatch(typeof(custommusicscript), "LoadOneSong", new Type[] { typeof(string) })]
    class PatchRadioCustomSend
    {
        private static void Prefix(string _path)
        {
            if (SteamP2PNetworkingUnderTheHoodScript.IsHost())
            {
                MelonLogger.Msg("Host playing song, syncing: " + Path.GetFileName(_path));
                
                var lobbyID = networkingScript.s.hood.ConnectedLobbyID;
                int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyID);

                for (int i = 0; i < memberCount; ++i)
                {
                    CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyID, i);
                    if (memberID != SteamUser.GetSteamID())
                    {
                        SendP2P.Send(memberID, new RadioPacket { fileName = Path.GetFileName(_path) });
                    }
                }
            }
        }
    }
}
