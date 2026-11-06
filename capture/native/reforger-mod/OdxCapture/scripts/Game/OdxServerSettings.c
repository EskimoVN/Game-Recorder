// Odx capture, server part: fixed time of day, invincible players and no AI for test servers
// (document/games/arma-reforger-daytime-invincible.md §3, arma-reforger-no-ai.md), from $profile:odx/settings.txt, one
// "key value" per line, "#" for comments: "time_of_day hh:mm" stops the clock there, "invincible 1" makes player
// characters take no damage, "ai 0" keeps the map free of AI characters.
// No file or no line = vanilla. Editing the file and restarting the server changes them without a new addon.
modded class SCR_BaseGameMode
{
	override protected void OnGameStart()
	{
		super.OnGameStart();
		if (!IsMaster())
			return;
		ODX_Settings.Load();
		// The scenario sets its own start time earlier, in SCR_TimeAndWeatherHandlerComponent.OnWorldPostProcess.
		if (ODX_Settings.s_bFixedTime)
			ODX_Settings.ApplyTime();
		if (ODX_Settings.s_bNoAI)
			ODX_Settings.DisableAI();
	}

	override protected void OnPlayerSpawned(int playerId, IEntity controlledEntity)
	{
		super.OnPlayerSpawned(playerId, controlledEntity);
		if (IsMaster() && ODX_Settings.s_bInvincible && controlledEntity)
			ODX_Settings.MakeInvincible(playerId, controlledEntity);
	}
}

class ODX_Settings
{
	static const string FILE = "$profile:odx/settings.txt";
	static bool s_bFixedTime;
	static int s_iHours;
	static int s_iMinutes;
	static bool s_bInvincible;
	static bool s_bNoAI;

	static void Load()
	{
		if (!FileIO.FileExists(FILE))
		{
			Print("ODX_Settings: no " + FILE + ", vanilla time and damage");
			return;
		}
		FileHandle file = FileIO.OpenFile(FILE, FileMode.READ);
		if (!file)
		{
			Print("ODX_Settings: cannot open " + FILE);
			return;
		}
		string line;
		int lineNumber = 0;
		while (file.ReadLine(line) >= 0)
		{
			lineNumber++;
			line = line.Trim();
			if (line == "" || line.StartsWith("#"))
				continue;
			array<string> parts = {};
			line.Split(" ", parts, true);
			if (parts.Count() == 2 && parts[0] == "time_of_day" && ParseTime(parts[1]))
				continue;
			if (parts.Count() == 2 && parts[0] == "invincible" && (parts[1] == "0" || parts[1] == "1"))
			{
				s_bInvincible = parts[1] == "1";
				Print("ODX_Settings: invincible players " + s_bInvincible);
				continue;
			}
			if (parts.Count() == 2 && parts[0] == "ai" && (parts[1] == "0" || parts[1] == "1"))
			{
				s_bNoAI = parts[1] == "0";
				Print("ODX_Settings: AI " + !s_bNoAI);
				continue;
			}
			Print("ODX_Settings: line " + lineNumber + " not understood: " + line);
		}
		file.Close();
	}

	protected static bool ParseTime(string text)
	{
		array<string> hm = {};
		text.Split(":", hm, false);
		if (hm.Count() != 2)
			return false;
		int parsedHours, parsedMinutes;
		int hours = hm[0].ToInt(parsed: parsedHours);
		int minutes = hm[1].ToInt(parsed: parsedMinutes);
		if (parsedHours != hm[0].Length() || parsedMinutes != hm[1].Length() || hours < 0 || hours > 23 || minutes < 0 || minutes > 59)
			return false;
		s_iHours = hours;
		s_iMinutes = minutes;
		s_bFixedTime = true;
		return true;
	}

	static void ApplyTime()
	{
		ChimeraWorld world = ChimeraWorld.CastFrom(GetGame().GetWorld());
		TimeAndWeatherManagerEntity manager;
		if (world)
			manager = world.GetTimeAndWeatherManager();
		if (!manager)
		{
			Print("ODX_Settings: no time and weather manager, time not set");
			return;
		}
		manager.SetHoursMinutesSeconds(s_iHours, s_iMinutes, 0, true);
		manager.SetIsDayAutoAdvanced(false);
		Print("ODX_Settings: time fixed at " + s_iHours + ":" + s_iMinutes);
	}

	static void MakeInvincible(int playerId, IEntity character)
	{
		SCR_CharacterDamageManagerComponent damage = SCR_CharacterDamageManagerComponent.Cast(
			character.FindComponent(SCR_CharacterDamageManagerComponent));
		if (!damage)
		{
			Print("ODX_Settings: player " + playerId + " has no character damage manager");
			return;
		}
		damage.EnableDamageHandling(false);
		Print("ODX_Settings: player " + playerId + " invincible");
	}

	// A character spawned for a player has no player for the 1–6 s until the player controller takes it, and looks like
	// AI; deleting it then breaks the spawn and leaves the spawn point reserved (SCR_SpawnPoint reservation failed, server
	// 2026-11-06). So a character is removed only after this long without a player.
	static const int UNOWNED_GRACE_MS = 10000;
	protected static ref map<ChimeraCharacter, float> s_mUnownedSince = new map<ChimeraCharacter, float>();

	// The AI limit stops the spawners that ask the AI world first (-disableAI sets the same limit); the AI the scenario
	// spawned before game start, or by a spawner that does not ask, are removed every 2 s.
	static void DisableAI()
	{
		AIWorld aiWorld = GetGame().GetAIWorld();
		if (!aiWorld)
		{
			Print("ODX_Settings: no AI world, AI not disabled");
			return;
		}
		aiWorld.SetAILimit(0);
		Print("ODX_Settings: AI limit 0");
		RemoveAI();
		GetGame().GetCallqueue().CallLater(RemoveAI, 2000, true);
	}

	protected static void RemoveAI()
	{
		array<AIAgent> agents = {};
		GetGame().GetAIWorld().GetAIAgents(agents);
		PlayerManager players = GetGame().GetPlayerManager();
		float now = GetGame().GetWorld().GetWorldTime();
		set<ChimeraCharacter> unowned = new set<ChimeraCharacter>();
		foreach (AIAgent agent : agents)
		{
			// Characters only: birds, animals and insects are AI agents too, and their native managers keep raw
			// pointers to them; deleting a bird crashed the server (SIGSEGV in AIBirdManagerComponent, 2026-10-27).
			ChimeraCharacter character = ChimeraCharacter.Cast(agent.GetControlledEntity());
			if (!character || players.GetPlayerIdFromControlledEntity(character) != 0)
				continue;
			unowned.Insert(character);
			float since;
			if (!s_mUnownedSince.Find(character, since))
			{
				s_mUnownedSince.Insert(character, now);
				continue;
			}
			if (now - since < UNOWNED_GRACE_MS)
				continue;
			s_mUnownedSince.Remove(character);
			ResourceName prefabName;
			EntityPrefabData prefab = character.GetPrefabData();
			if (prefab)
				prefabName = prefab.GetPrefabName();
			Print("ODX_Settings: AI removed " + prefabName);
			SCR_EntityHelper.DeleteEntityAndChildren(character);
		}
		// Forget the characters a player took, and those gone some other way.
		for (int i = s_mUnownedSince.Count() - 1; i >= 0; i--)
		{
			ChimeraCharacter character = s_mUnownedSince.GetKey(i);
			if (!character || !unowned.Contains(character))
				s_mUnownedSince.RemoveElement(i);
		}
	}
}
