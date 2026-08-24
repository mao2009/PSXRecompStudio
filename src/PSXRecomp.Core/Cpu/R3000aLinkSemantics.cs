using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
public static class R3000aLinkSemantics
{
    public static bool TryGetLinkValue(in R3000aInstruction instruction, uint pc, out uint linkValue)
    {
        if (!instruction.LinkInfo.WritesLink)
        {
            linkValue = 0;
            return false;
        }

        linkValue = unchecked(pc + 8u);
        return true;
    }
}
