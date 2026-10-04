using Arrowgene.Logging;
using Arrowgene.MonsterHunterOnline.Protocol.Constant;
using Arrowgene.MonsterHunterOnline.Protocol.Old.Structures;
using Arrowgene.MonsterHunterOnline.Protocol.Structures;
using Arrowgene.MonsterHunterOnline.Service.System.CharacterSystem;
using Microsoft.VisualBasic.FileIO;
using System.Globalization;
using System.IO;
using System.Threading;
using Arrowgene.MonsterHunterOnline.Service.CsProto.Core;

namespace Arrowgene.MonsterHunterOnline.Service.CsProto.Handler;

public class EnterLevelNtfHandler : CsProtoStructureHandler<EnterLevelNtf>
{
    private static readonly ServiceLogger Logger =
        LogProvider.Logger<ServiceLogger>(typeof(EnterLevelNtfHandler));

    public override CS_CMD_ID Cmd => CS_CMD_ID.CS_CMD_ENTER_LEVEL_NTF;

    private readonly CharacterManager _characterManager;

    public EnterLevelNtfHandler(CharacterManager characterManager)
    {
        _characterManager = characterManager;
    }

    public override void Handle(Client client, EnterLevelNtf req)
    {
        // _characterManager.SyncAllAttr(client);

        string staticFolder = Path.Combine(Util.ExecutingDirectory(), "Files/Static");
        string npcFilePath = Path.Combine(staticFolder, "LevelDataNPCs.csv");
        using (TextFieldParser parser = new TextFieldParser(npcFilePath))
        {
            parser.TextFieldType = FieldType.Delimited;
            parser.SetDelimiters(",");

            // Skip the header line
            parser.ReadLine();
            while (!parser.EndOfData)
            {
                string[] fields = parser.ReadFields();
                if (fields == null || fields.Length < 10)
                    continue;

                string rawLevelIds = fields[0];
                string levelId = rawLevelIds;

                // Preserve the original town/hub matching behavior.
                if (levelId.Length > 0)
                    levelId = levelId.Remove(levelId.Length - 1);

                // 150 hubs, 160 farm, 180 city
                bool isTownMatch = (client.State.levelId.ToString() == levelId)
                                   && (levelId.StartsWith("150") || levelId.StartsWith("160") || levelId.StartsWith("180"))
                                   && levelId.EndsWith("01");
                if (isTownMatch)
                {
                    // TODO: HACK because it doesnt seems to work with a full list of the zone.
                    CsCsProtoStructurePacket<MonsterAppearNtfList> monsterAppearNtfList =
                        CsProtoResponse.MonsterAppearNtfList;

                    string npcID = fields[9];

                    string[] posValues = fields[4].Split(",");
                    string[] rotValues = fields[5].Split(",");

                    float posX = float.Parse(posValues[0], CultureInfo.InvariantCulture);
                    float posY = float.Parse(posValues[1], CultureInfo.InvariantCulture);
                    float posZ = float.Parse(posValues[2], CultureInfo.InvariantCulture);

                    // Tricky thing, W is first here, not the same as ChangeTown.csv.
                    float rotateW = float.Parse(rotValues[0], CultureInfo.InvariantCulture);
                    float rotateX = float.Parse(rotValues[1], CultureInfo.InvariantCulture);
                    float rotateY = float.Parse(rotValues[2], CultureInfo.InvariantCulture);
                    float rotateZ = float.Parse(rotValues[3], CultureInfo.InvariantCulture);

                    CSVec3 npcPosVec = new CSVec3(posX, posY, posZ);
                    CSQuat npcRotQuat = new CSQuat(rotateW, rotateX, rotateY, rotateZ);

                    monsterAppearNtfList.Structure.Appear.Add(new MonsterAppearNtf()
                    {
                        NetId = 0,
                        SpawnType = 1,
                        MonsterInfoId = int.Parse(npcID),
                        Pose = new CSQuatT(npcPosVec, npcRotQuat),
                    });

                    client.SendCsProtoStructurePacket(monsterAppearNtfList);
                    Thread.Sleep(25);
                    continue;
                }

                // First vertical-slice hunt probe:
                // Level 100292 is the Bulldrome hunt in Hermit Forest.
                // Client data contains one unique "Guide_SP" row for this level with
                // FixedMonsterID 60032, the quest-specific Bulldrome variant.
                //
                // Rathalos RE established that hunt monsters must use:
                //   SpawnType = 1  -> resolve the render template by MonsterInfoId
                //   NetId = 0      -> let the client allocate the local render puppet
                //
                // Keep this deliberately narrow until the visual spawn is validated.
                bool isBulldromeProbe =
                    client.State.levelId == 100292
                    && rawLevelIds == "100292;"
                    && fields[3] == "Guide_SP"
                    && fields[9] == "60032";

                if (!isBulldromeProbe)
                    continue;

                string[] huntPosValues = fields[4].Split(",");
                string[] huntRotValues = fields[5].Split(",");

                float huntPosX = float.Parse(huntPosValues[0], CultureInfo.InvariantCulture);
                float huntPosY = float.Parse(huntPosValues[1], CultureInfo.InvariantCulture);
                float huntPosZ = float.Parse(huntPosValues[2], CultureInfo.InvariantCulture);

                float huntRotateW = float.Parse(huntRotValues[0], CultureInfo.InvariantCulture);
                float huntRotateX = float.Parse(huntRotValues[1], CultureInfo.InvariantCulture);
                float huntRotateY = float.Parse(huntRotValues[2], CultureInfo.InvariantCulture);
                float huntRotateZ = float.Parse(huntRotValues[3], CultureInfo.InvariantCulture);

                CSVec3 huntPosVec = new CSVec3(huntPosX, huntPosY, huntPosZ);
                CSQuat huntRotQuat = new CSQuat(huntRotateW, huntRotateX, huntRotateY, huntRotateZ);

                CsCsProtoStructurePacket<MonsterAppearNtfList> huntMonsterAppearNtfList =
                    CsProtoResponse.MonsterAppearNtfList;

                huntMonsterAppearNtfList.Structure.Appear.Add(new MonsterAppearNtf()
                {
                    NetId = 0,
                    SpawnType = 1,
                    MonsterInfoId = 60032,
                    Pose = new CSQuatT(huntPosVec, huntRotQuat),
                });

                Logger.Info(client,
                    $"Hunt monster appear probe: LevelId={client.State.levelId}, MonsterInfoId=60032, " +
                    $"Spawn=Guide_SP, Position=({huntPosX},{huntPosY},{huntPosZ})");

                client.SendCsProtoStructurePacket(huntMonsterAppearNtfList);
                return;
            }
        }
    }
}
