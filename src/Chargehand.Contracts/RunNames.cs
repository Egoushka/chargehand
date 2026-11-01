namespace Chargehand.Contracts;

/// <summary>
/// A friendly name for a run, like "Brave Capybara": an adjective and an animal picked from the run id, so the same id always gets the same name.
/// Names are for people to tell runs apart at a glance. They are not unique, so a program matches on the id, never on the name.
/// 256 × 256 = 65,536 names: among 20 runs active at once a clash is about 0.3% likely.
/// </summary>
public static class RunNames
{
    /// <summary>"Brave Capybara".</summary>
    public static string Of(string runId)
    {
        var (adjective, animal) = Pick(runId);
        return $"{Capitalize(adjective)} {Capitalize(animal)}";
    }

    /// <summary>"brave-capybara": lowercase letters and one hyphen, safe in a branch name or a file name.</summary>
    public static string Slug(string runId)
    {
        var (adjective, animal) = Pick(runId);
        return $"{adjective}-{animal}";
    }

    /// <summary>"Brave Capybara (run-20261101-120000-a1b2c3)": the name next to the id, for text a person reads.</summary>
    public static string Label(string runId) => $"{Of(runId)} ({runId})";

    internal static IReadOnlyList<string> Adjectives => AdjectiveList;

    internal static IReadOnlyList<string> Animals => AnimalList;

    private static (string Adjective, string Animal) Pick(string runId)
    {
        var h = Mix(Fnv1a(runId));
        return (AdjectiveList[(int)(h & 0xFF)], AnimalList[(int)((h >> 8) & 0xFF)]);
    }

    private static string Capitalize(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    // FNV-1a is fixed by its definition, unlike string.GetHashCode, which differs per process; the mixer spreads ids that differ only in their last characters.
    private static ulong Fnv1a(string text)
    {
        var h = 14695981039346656037UL;
        foreach (var c in text)
        {
            h = (h ^ c) * 1099511628211UL;
        }
        return h;
    }

    private static ulong Mix(ulong x)
    {
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static readonly string[] AdjectiveList =
    [
        "able", "adept", "agile", "airy", "alert", "amber", "ample", "angelic", "apt", "arctic", "ardent", "astute", "atomic", "azure", "balmy", "bashful",
        "bold", "bouncy", "brave", "breezy", "bright", "brisk", "bubbly", "burly", "busy", "calm", "candid", "careful", "cheery", "chilly", "chipper", "civil",
        "classy", "clever", "cobalt", "cosmic", "cozy", "crafty", "crisp", "curious", "dandy", "dapper", "daring", "dashing", "dainty", "dazzling", "deft", "dizzy",
        "dreamy", "dusty", "eager", "earnest", "easy", "elated", "elfin", "epic", "even", "fair", "fancy", "fast", "feisty", "fiery", "fine", "fleet",
        "fluffy", "frank", "fresh", "frosty", "fuzzy", "gallant", "gentle", "giddy", "glad", "gleeful", "glossy", "golden", "graceful", "grand", "great", "groovy",
        "hardy", "hasty", "hearty", "hip", "honest", "humble", "hushed", "icy", "ideal", "inky", "jade", "jaunty", "jazzy", "jolly", "jovial", "joyful",
        "keen", "kind", "knightly", "lazy", "leafy", "level", "light", "lively", "lone", "loyal", "lucid", "lucky", "lunar", "lush", "magic", "mellow",
        "merry", "mighty", "mild", "minty", "misty", "modest", "mossy", "native", "neat", "nifty", "nimble", "noble", "novel", "nutty", "oaken", "odd",
        "olive", "open", "opal", "pale", "patient", "peppy", "perky", "plucky", "plush", "polite", "pretty", "prime", "proud", "puffy", "pure", "quick",
        "quiet", "quirky", "radiant", "rapid", "rare", "ready", "regal", "rosy", "round", "royal", "rugged", "rustic", "sandy", "sassy", "savvy", "serene",
        "sharp", "shiny", "shy", "silent", "silky", "silver", "sleek", "sly", "smart", "smooth", "snappy", "snowy", "snug", "solar", "sound", "spry",
        "stable", "steady", "stellar", "stoic", "sturdy", "sunny", "super", "swift", "tame", "tawny", "teal", "tender", "tidy", "tiny", "topaz", "tough",
        "trim", "true", "trusty", "ultra", "upbeat", "urban", "valiant", "velvet", "vivid", "warm", "wavy", "wild", "windy", "wise", "witty", "wonder",
        "woolly", "yellow", "young", "zany", "zappy", "zealous", "zen", "zesty", "ashen", "bonny", "cuddly", "dewy", "ebony", "elite", "famed", "flinty",
        "gutsy", "hazel", "ivory", "jumpy", "kooky", "limber", "meek", "neon", "pearly", "quaint", "rowdy", "spunky", "tangy", "umber", "vital", "zippy",
        "brainy", "chummy", "comfy", "fabled", "fervent", "gleaming", "hale", "jocund", "lofty", "mirthful", "nippy", "peachy", "sparkly", "spirited", "sprightly", "tickled",
    ];

    private static readonly string[] AnimalList =
    [
        "alpaca", "ant", "antelope", "armadillo", "axolotl", "badger", "bat", "bear", "beaver", "bee", "beetle", "aardvark", "boar", "bobcat", "bonobo", "buffalo",
        "bunny", "butterfly", "camel", "capybara", "caracal", "cat", "chameleon", "cheetah", "chickadee", "chipmunk", "cicada", "civet", "cobra", "condor", "cougar", "cow",
        "coyote", "crab", "crane", "cricket", "crow", "cuckoo", "deer", "dingo", "dolphin", "donkey", "dove", "dragonfly", "duck", "eagle", "echidna", "eel",
        "egret", "elephant", "elk", "emu", "falcon", "ferret", "finch", "firefly", "flamingo", "fox", "frog", "gazelle", "gecko", "gerbil", "gibbon", "giraffe",
        "goat", "goose", "gopher", "gorilla", "grasshopper", "grouse", "gull", "hamster", "hare", "hawk", "hedgehog", "heron", "hippo", "hornet", "horse", "hummingbird",
        "hyena", "ibex", "ibis", "iguana", "impala", "jackal", "jaguar", "jay", "jellyfish", "kangaroo", "kestrel", "kingfisher", "kitten", "kiwi", "koala", "kudu",
        "ladybug", "lamb", "lark", "lemming", "lemur", "leopard", "lion", "lizard", "llama", "lobster", "loon", "lynx", "macaw", "magpie", "manatee", "mantis",
        "marmot", "marten", "meerkat", "mink", "mole", "mongoose", "monkey", "moose", "moth", "mouse", "mule", "narwhal", "newt", "nightingale", "ocelot", "octopus",
        "okapi", "opossum", "orca", "oriole", "oryx", "osprey", "ostrich", "otter", "owl", "ox", "panda", "pangolin", "panther", "parrot", "peacock", "pelican",
        "penguin", "pheasant", "pigeon", "platypus", "pony", "porcupine", "possum", "puffin", "puma", "quail", "quokka", "rabbit", "raccoon", "ram", "raven", "reindeer",
        "rhino", "robin", "rooster", "salamander", "salmon", "sandpiper", "seagull", "seahorse", "seal", "serval", "shark", "sheep", "shrew", "skunk", "sloth", "snail",
        "sparrow", "squid", "squirrel", "starfish", "starling", "stoat", "stork", "swallow", "swan", "tapir", "tarsier", "termite", "tern", "tiger", "toad", "toucan",
        "trout", "tuna", "turkey", "turtle", "viper", "vole", "vulture", "wallaby", "walrus", "warbler", "wasp", "weasel", "whale", "wildcat", "wolf", "wolverine",
        "wombat", "woodpecker", "wren", "yak", "zebra", "anteater", "barracuda", "bluebird", "bulldog", "cardinal", "catfish", "cormorant", "dormouse", "gannet", "gnu", "grizzly",
        "guppy", "hoopoe", "husky", "jackrabbit", "lapwing", "mackerel", "mallard", "manta", "mustang", "pika", "piranha", "plover", "stingray", "tamarin", "whippet", "albatross",
        "bandicoot", "bullfrog", "dugong", "flounder", "kookaburra", "mudskipper", "nuthatch", "pufferfish", "quetzal", "roadrunner", "sunbird", "wagtail", "tortoise", "stickleback", "swiftlet", "hornbill",
    ];
}
