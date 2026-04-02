using System;

namespace AccordIn.Plugin
{
    internal static class Helpers
    {
        public static int MapFrequency(string frequency)
        {
            switch ((frequency ?? string.Empty).ToLowerInvariant().Trim())
            {
                case "monthly":   return 1;
                case "biweekly":  return 2;
                case "quarterly": return 3;
                case "weekly":    return 4;
                case "ad-hoc":    return 5;
                default:          return 1;
            }
        }

        public static int MapCadenceChannel(string channel)
        {
            switch ((channel ?? string.Empty).ToLowerInvariant().Trim())
            {
                case "online-meeting":
                case "meeting":        return 0;
                case "phone":          return 1;
                case "in-person":      return 2;
                case "event":          return 3;
                case "email":          return 4;
                default:               return 0;
            }
        }

        public static int MapActionChannel(string channel)
        {
            switch ((channel ?? string.Empty).ToLowerInvariant().Trim())
            {
                case "meeting":
                case "online-meeting": return 1;
                case "email":          return 2;
                case "event":          return 3;
                case "call":
                case "phone":          return 4;
                case "other":          return 5;
                case "in-person":      return 1;
                default:               return 1;
            }
        }

        public static int MapPriority(string priority)
        {
            switch ((priority ?? string.Empty).ToLowerInvariant().Trim())
            {
                case "high":   return 1;
                case "medium": return 2;
                case "low":    return 3;
                default:       return 2;
            }
        }

        public static int MapConfidence(string confidence)
        {
            switch ((confidence ?? string.Empty).ToLowerInvariant().Trim())
            {
                case "high":   return 1;
                case "medium": return 2;
                case "low":    return 3;
                default:       return 2;
            }
        }

        public static int MapChannel(string channel)
        {
            return MapCadenceChannel(channel);
        }

        public static int MapRecommendationType(string type)
        {
            switch (NormalizeRecommendationType(type))
            {
                case "cross-sell":   return 1;
                case "upsell":       return 2;
                case "retention":    return 3;
                case "relationship": return 4;
                default:             return 1;
            }
        }

        public static int MapPlanType(string planType)
        {
            switch ((planType ?? string.Empty).ToLowerInvariant().Trim())
            {
                case "cross-sell":   return 0;
                case "retention":
                case "renewal":      return 1;
                case "relationship": return 2;
                case "upsell":       return 3;
                case "new-contact":  return 4;
                default:             return 0;
            }
        }

        public static string MapPlanTypeIntToString(int value)
        {
            switch (value)
            {
                case 1: return "retention";
                case 2: return "relationship";
                case 3: return "upsell";
                case 4: return "new-contact";
                default: return "cross-sell";
            }
        }

        private static string NormalizeRecommendationType(string type)
        {
            var normalized = (type ?? string.Empty).ToLowerInvariant().Trim();

            switch (normalized)
            {
                case "cross sell":
                case "cross_sell":
                case "crosssell":
                    return "cross-sell";
                default:
                    return normalized;
            }
        }
    }
}
