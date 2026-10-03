//====================[ Imports ]====================
using Fika.Core.Networking.LiteNetLib.Utils;

namespace KeepMeAlive.Fika.Packets
{
    //====================[ Core State Packets ]====================
    public struct BleedingOutPacket : INetSerializable
    {
        public string playerId;
        public float timeRemaining;
        public int livesRemaining;

        public void Deserialize(NetDataReader reader)
        {
            playerId = reader.GetString();
            timeRemaining = reader.GetFloat();
            livesRemaining = reader.GetInt();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(playerId ?? string.Empty);
            writer.Put(timeRemaining);
            writer.Put(livesRemaining);
        }
    }

    public struct RevivedPacket : INetSerializable
    {
        public string playerId;
        public string reviverId;

        public void Deserialize(NetDataReader reader)
        {
            playerId = reader.GetString();
            reviverId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(playerId ?? "");
            writer.Put(reviverId ?? "");
        }
    }

    public struct PlayerStateResetPacket : INetSerializable
    {
        public string playerId;
        public bool isDead;
        public float cooldownSeconds;

        public void Deserialize(NetDataReader reader)
        {
            playerId = reader.GetString();
            isDead = reader.GetBool();
            cooldownSeconds = reader.GetFloat();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(playerId ?? "");
            writer.Put(isDead);
            writer.Put(cooldownSeconds);
        }
    }

    // Periodic state heartbeat broadcast by players in a non-None state. Sent every ~5s and on transitions for late-joiners.
    public struct PlayerStateResyncPacket : INetSerializable
    {
        public string playerId;
        public int    state;                 // RMState cast to int
        public float  criticalTimer;
        public float  invulTimer;
        public float  cooldownTimer;
        public string reviverId;
        public int    reviveRequestedSource; // ReviveSource cast to int (0=Self, 1=Team)
        public int    livesRemaining;
        public string draggerId;             // Owner's current dragger (empty when released)
        public bool   limp;                  // Whether the player is still limp

        public void Deserialize(NetDataReader reader)
        {
            playerId              = reader.GetString();
            state                 = reader.GetInt();
            criticalTimer         = reader.GetFloat();
            invulTimer            = reader.GetFloat();
            cooldownTimer         = reader.GetFloat();
            reviverId             = reader.GetString();
            reviveRequestedSource = reader.GetInt();
            livesRemaining        = reader.GetInt();
            draggerId             = reader.GetString();
            limp                  = reader.GetBool();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(playerId ?? "");
            writer.Put(state);
            writer.Put(criticalTimer);
            writer.Put(invulTimer);
            writer.Put(cooldownTimer);
            writer.Put(reviverId ?? "");
            writer.Put(reviveRequestedSource);
            writer.Put(livesRemaining);
            writer.Put(draggerId ?? "");
            writer.Put(limp);
        }
    }

    //====================[ Revival Action Packets ]====================
    public struct SelfReviveStartPacket : INetSerializable
    {
        public string playerId;

        public void Deserialize(NetDataReader reader)
        {
            playerId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(playerId ?? "");
        }
    }

    public struct TeamHelpPacket : INetSerializable
    {
        public string reviveeId;
        public string reviverId;

        public void Deserialize(NetDataReader reader)
        {
            reviveeId = reader.GetString();
            reviverId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(reviveeId ?? "");
            writer.Put(reviverId ?? "");
        }
    }

    public struct TeamCancelPacket : INetSerializable
    {
        public string reviveeId;
        public string reviverId;

        public void Deserialize(NetDataReader reader)
        {
            reviveeId = reader.GetString();
            reviverId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(reviveeId ?? "");
            writer.Put(reviverId ?? "");
        }
    }

    public struct TeamReviveStartPacket : INetSerializable
    {
        public string reviveeId;
        public string reviverId;

        public void Deserialize(NetDataReader reader)
        {
            reviveeId = reader.GetString();
            reviverId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(reviveeId ?? "");
            writer.Put(reviverId ?? "");
        }
    }

    //====================[ Team Healing Packets ]====================
    public struct TeamHealPacket : INetSerializable
    {
        public string patientId;
        public string healerId;
        public string itemId;

        public void Deserialize(NetDataReader reader)
        {
            patientId = reader.GetString();
            healerId = reader.GetString();
            itemId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(patientId ?? "");
            writer.Put(healerId ?? "");
            writer.Put(itemId ?? "");
        }
    }

    // Patient -> healer: whether the requested item was actually applied.
    public struct TeamHealResultPacket : INetSerializable
    {
        public string patientId;
        public string healerId;
        public string itemId;
        public bool success;
        public string reason;

        public void Deserialize(NetDataReader reader)
        {
            patientId = reader.GetString();
            healerId = reader.GetString();
            itemId = reader.GetString();
            success = reader.GetBool();
            reason = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(patientId ?? "");
            writer.Put(healerId ?? "");
            writer.Put(itemId ?? "");
            writer.Put(success);
            writer.Put(reason ?? "");
        }
    }

    //====================[ Drag Packets ]====================
    // Shares drag start and release events with all peers.
    public struct DragStatePacket : INetSerializable
    {
        public string reviveeId;
        public string draggerId;
        public bool active;

        public void Deserialize(NetDataReader reader)
        {
            reviveeId = reader.GetString();
            draggerId = reader.GetString();
            active = reader.GetBool();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(reviveeId ?? "");
            writer.Put(draggerId ?? "");
            writer.Put(active);
        }
    }
}
