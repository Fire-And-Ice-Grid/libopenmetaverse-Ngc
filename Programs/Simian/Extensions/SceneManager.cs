using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using OpenMetaverse;
using OpenMetaverse.Imaging;
using OpenMetaverse.Packets;

namespace Simian.Extensions
{
    public class SceneManager : ISimianExtension, ISceneProvider
    {
        Simian server;
        DoubleDictionary<uint, UUID, SimulationObject> sceneObjects = new DoubleDictionary<uint, UUID, SimulationObject>();
        int currentLocalID = 1;
        float[] heightmap = new float[256 * 256];

        public event ObjectAddCallback OnObjectAdd;
        public event ObjectRemoveCallback OnObjectRemove;
        public event ObjectTransformCallback OnObjectTransform;
        public event ObjectFlagsCallback OnObjectFlags;
        public event ObjectModifyCallback OnObjectModify;
        public event TerrainUpdatedCallback OnTerrainUpdated;

        public float[] Heightmap
        {
            get { return heightmap; }
            set
            {
                if (value.Length != (256 * 256))
                    throw new ArgumentException("Heightmap must be 256x256");
                heightmap = value;
            }
        }

        public SceneManager(Simian server)
        {
            this.server = server;
        }

        public void Start()
        {
            server.UDP.RegisterPacketCallback(PacketType.CompleteAgentMovement, new PacketCallback(CompleteAgentMovementHandler));
            LoadTerrain(server.DataDir + "heightmap.tga");
        }

        public void Stop()
        {
        }

        public bool ObjectAdd(object sender, Agent creator, SimulationObject obj)
        {
            // Assign a unique LocalID to this object
            obj.Prim.LocalID = (uint)Interlocked.Increment(ref currentLocalID);

            if (OnObjectAdd != null)
            {
                OnObjectAdd(sender, creator, obj);
            }

            // Add the object to the scene dictionary
            sceneObjects.Add(obj.Prim.LocalID, obj.Prim.ID, obj);

            // Send an update out to the creator
            ObjectUpdatePacket updateToOwner = SimulationObject.BuildFullUpdate(obj.Prim, server.RegionHandle, 0,
                obj.Prim.Flags | PrimFlags.CreateSelected | PrimFlags.ObjectYouOwner);
            server.UDP.SendPacket(creator.AgentID, updateToOwner, PacketCategory.State);

            // Send an update out to everyone else
            ObjectUpdatePacket updateToOthers = SimulationObject.BuildFullUpdate(obj.Prim, server.RegionHandle, 0,
                obj.Prim.Flags);
            lock (server.Agents)
            {
                foreach (Agent recipient in server.Agents.Values)
                {
                    if (recipient != creator)
                        server.UDP.SendPacket(recipient.AgentID, updateToOthers, PacketCategory.State);
                }
            }

            return true;
        }

        public bool ObjectRemove(object sender, SimulationObject obj)
        {
            if (OnObjectRemove != null)
            {
                OnObjectRemove(sender, obj);
            }

            sceneObjects.Remove(obj.Prim.LocalID, obj.Prim.ID);

            KillObjectPacket kill = new KillObjectPacket();
            kill.ObjectData = new KillObjectPacket.ObjectDataBlock[1];
            kill.ObjectData[0] = new KillObjectPacket.ObjectDataBlock();
            kill.ObjectData[0].ID = obj.Prim.LocalID;

            server.UDP.BroadcastPacket(kill, PacketCategory.State);

            return true;
        }

        public void ObjectTransform(object sender, SimulationObject obj, Vector3 position,
            Quaternion rotation, Vector3 velocity, Vector3 acceleration, Vector3 angularVelocity,
            Vector3 scale)
        {
            if (OnObjectTransform != null)
            {
                OnObjectTransform(sender, obj, position, rotation, velocity,
                    acceleration, angularVelocity, scale);
            }

            // Update the object
            obj.Prim.Position = position;
            obj.Prim.Rotation = rotation;
            obj.Prim.Velocity = velocity;
            obj.Prim.Acceleration = acceleration;
            obj.Prim.AngularVelocity = angularVelocity;
            obj.Prim.Scale = scale;

            // Inform clients
            BroadcastObjectUpdate(obj);
        }

        public void ObjectFlags(object sender, SimulationObject obj, PrimFlags flags)
        {
            if (OnObjectFlags != null)
            {
                OnObjectFlags(sender, obj, flags);
            }

            // Update the object
            obj.Prim.Flags = flags;

            // Inform clients
            BroadcastObjectUpdate(obj);
        }

        public void ObjectModify(object sender, SimulationObject obj, Primitive.ConstructionData data)
        {
            if (OnObjectModify != null)
            {
                OnObjectModify(sender, obj, data);
            }

            // Update the object
            obj.Prim.PrimData = data;

            // Inform clients
            BroadcastObjectUpdate(obj);
        }

        public bool TryGetObject(uint localID, out SimulationObject obj)
        {
            return sceneObjects.TryGetValue(localID, out obj);
        }

        public bool TryGetObject(UUID id, out SimulationObject obj)
        {
            return sceneObjects.TryGetValue(id, out obj);
        }

        void BroadcastObjectUpdate(SimulationObject obj)
        {
            ObjectUpdatePacket update =
                SimulationObject.BuildFullUpdate(obj.Prim, server.RegionHandle, 0, obj.Prim.Flags);

            server.UDP.BroadcastPacket(update, PacketCategory.State);
        }

        void CompleteAgentMovementHandler(Packet packet, Agent agent)
        {
            CompleteAgentMovementPacket request = (CompleteAgentMovementPacket)packet;

            // Create a representation for this agent
            Avatar avatar = new Avatar();
            avatar.ID = agent.AgentID;
            avatar.LocalID = (uint)Interlocked.Increment(ref currentLocalID);
            avatar.Position = new Vector3(128f, 128f, 25f);
            avatar.Rotation = Quaternion.Identity;
            avatar.Scale = new Vector3(0.45f, 0.6f, 1.9f);
            avatar.PrimData.Material = Material.Flesh;
            avatar.PrimData.PCode = PCode.Avatar;

            // Create a default outfit for the avatar
            Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID("c228d1cf-4b5d-4ba8-84f4-899a0796aa97"));
            avatar.Textures = te;

            // Set the avatar name
            NameValue[] name = new NameValue[2];
            name[0] = new NameValue("FirstName", NameValue.ValueType.String, NameValue.ClassType.ReadWrite,
                NameValue.SendtoType.SimViewer, agent.FirstName);
            name[1] = new NameValue("LastName", NameValue.ValueType.String, NameValue.ClassType.ReadWrite,
                NameValue.SendtoType.SimViewer, agent.LastName);
            avatar.NameValues = name;

            // Link this avatar up with the corresponding agent
            agent.Avatar = avatar;

            // Give testers a provisionary balance of 1000L
            agent.Balance = 1000;

            AgentMovementCompletePacket complete = new AgentMovementCompletePacket();
            complete.AgentData.AgentID = agent.AgentID;
            complete.AgentData.SessionID = agent.SessionID;
            complete.Data.LookAt = Vector3.UnitX;
            complete.Data.Position = avatar.Position;
            complete.Data.RegionHandle = server.RegionHandle;
            complete.Data.Timestamp = Utils.DateTimeToUnixTime(DateTime.Now);
            complete.SimData.ChannelVersion = Utils.StringToBytes("Simian");

            server.UDP.SendPacket(agent.AgentID, complete, PacketCategory.Transaction);

            // Send updates and appearances for every avatar to this new avatar
            SynchronizeStateTo(agent);

            //HACK: Notify everyone when someone logs on to the simulator
            OnlineNotificationPacket online = new OnlineNotificationPacket();
            online.AgentBlock = new OnlineNotificationPacket.AgentBlockBlock[1];
            online.AgentBlock[0] = new OnlineNotificationPacket.AgentBlockBlock();
            online.AgentBlock[0].AgentID = agent.AgentID;
            server.UDP.BroadcastPacket(online, PacketCategory.State);
        }

        // HACK: The reduction provider will deprecate this at some point
        void SynchronizeStateTo(Agent agent)
        {
            lock (server.Agents)
            {
                foreach (Agent otherAgent in server.Agents.Values)
                {
                    // Send ObjectUpdate packets for this avatar
                    ObjectUpdatePacket update = SimulationObject.BuildFullUpdate(otherAgent.Avatar,
                        server.RegionHandle, otherAgent.State, otherAgent.Flags);
                    server.UDP.SendPacket(agent.AgentID, update, PacketCategory.State);

                    // Send appearances for this avatar
                    AvatarAppearancePacket appearance = AvatarManager.BuildAppearancePacket(otherAgent);
                    server.UDP.SendPacket(agent.AgentID, appearance, PacketCategory.State);
                }
            }

            sceneObjects.ForEach(delegate(SimulationObject obj)
            {
                ObjectUpdatePacket update = SimulationObject.BuildFullUpdate(obj.Prim,
                    obj.Prim.RegionHandle, 0, obj.Prim.Flags);
                server.UDP.SendPacket(agent.AgentID, update, PacketCategory.State);
            });

            // Send terrain data
            SendLayerData(agent);
        }

        void LoadTerrain(string mapFile)
        {
            if (File.Exists(mapFile))
            {
                lock (heightmap)
                {
                    Bitmap bmp = LoadTGAClass.LoadTGA(mapFile);

                    Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                    BitmapData bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    IntPtr ptr = bmpData.Scan0;
                    int bytes = bmpData.Stride * bmp.Height;
                    byte[] rgbValues = new byte[bytes];
                    Marshal.Copy(ptr, rgbValues, 0, bytes);
                    bmp.UnlockBits(bmpData);

                    for (int i = 1, pos = 0; i < heightmap.Length; i++, pos += 3)
                        heightmap[i] = (float)rgbValues[pos];

                    if (OnTerrainUpdated != null)
                        OnTerrainUpdated(this);
                }
            }
            else
            {
                Logger.Log("Map file " + mapFile + " not found, defaulting to 25m", Helpers.LogLevel.Info);

                server.Scene.Heightmap = new float[65536];
                for (int i = 0; i < server.Scene.Heightmap.Length; i++)
                    server.Scene.Heightmap[i] = 25f;
            }
        }

        void SendLayerData(Agent agent)
        {
            lock (heightmap)
            {
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        int[] patches = new int[1];
                        patches[0] = (y * 16) + x;
                        LayerDataPacket layer = TerrainCompressor.CreateLandPacket(heightmap, patches);
                        server.UDP.SendPacket(agent.AgentID, layer, PacketCategory.Terrain);
                    }
                }
            }
        }
    }
}