#Requires -Version 7.0

<#
.SYNOPSIS
    Dependency graph for Batch Orchestrator issue scheduling.

.DESCRIPTION
    Provides DAG construction, cycle detection, topological sorting,
    and dependency resolution for parallel Issue execution.

.NOTES
    Version: 1.0.0
    Issue: #155
    Runtime: PowerShell Core 7.x
    Platform: Cross-platform (Windows, Linux, macOS)
#>

function New-DependencyGraph {
    <#
    .SYNOPSIS
        Creates a new empty dependency graph.
    .OUTPUTS
        Hashtable with Nodes and Edges.
    #>
    [CmdletBinding()]
    param()

    return @{
        Nodes = @{}
        Edges = @{}
    }
}

function Add-DependencyNode {
    <#
    .SYNOPSIS
        Adds an issue node to the dependency graph.
    .PARAMETER Graph
        The dependency graph.
    .PARAMETER IssueId
        The issue identifier (number or string).
    .PARAMETER IssueData
        Optional metadata for the issue.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Graph,

        [Parameter(Mandatory = $true)]
        [string]$IssueId,

        [Parameter(Mandatory = $false)]
        [hashtable]$IssueData = @{}
    )

    if ($Graph.Nodes.ContainsKey($IssueId)) {
        throw "Issue $IssueId already exists in graph"
    }

    $Graph.Nodes[$IssueId] = @{
        Id = $IssueId
        Data = $IssueData
    }

    if (-not $Graph.Edges.ContainsKey($IssueId)) {
        $Graph.Edges[$IssueId] = @()
    }
}

function Add-DependencyEdge {
    <#
    .SYNOPSIS
        Adds a dependency edge (from depends on to).
    .PARAMETER Graph
        The dependency graph.
    .PARAMETER FromIssue
        The issue that depends on another.
    .PARAMETER ToIssue
        The issue being depended upon.
    .DESCRIPTION
        An edge from A to B means "A depends on B".
        B must complete before A can start.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Graph,

        [Parameter(Mandatory = $true)]
        [string]$FromIssue,

        [Parameter(Mandatory = $true)]
        [string]$ToIssue
    )

    if (-not $Graph.Nodes.ContainsKey($FromIssue)) {
        throw "FromIssue $FromIssue not found in graph"
    }
    if (-not $Graph.Nodes.ContainsKey($ToIssue)) {
        throw "ToIssue $ToIssue not found in graph"
    }
    if ($FromIssue -eq $ToIssue) {
        throw "Issue cannot depend on itself"
    }

    if (-not $Graph.Edges.ContainsKey($FromIssue)) {
        $Graph.Edges[$FromIssue] = @()
    }

    if ($ToIssue -notin $Graph.Edges[$FromIssue]) {
        $Graph.Edges[$FromIssue] += $ToIssue
    }
}

function Get-DependencyPredecessors {
    <#
    .SYNOPSIS
        Gets all issues that a given issue depends on.
    .PARAMETER Graph
        The dependency graph.
    .PARAMETER IssueId
        The issue to query.
    .OUTPUTS
        Array of issue IDs that IssueId depends on.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Graph,

        [Parameter(Mandatory = $true)]
        [string]$IssueId
    )

    if ($Graph.Edges.ContainsKey($IssueId)) {
        return $Graph.Edges[$IssueId]
    }
    return @()
}

function Get-DependencySuccessors {
    <#
    .SYNOPSIS
        Gets all issues that depend on a given issue.
    .PARAMETER Graph
        The dependency graph.
    .PARAMETER IssueId
        The issue to query.
    .OUTPUTS
        Array of issue IDs that depend on IssueId.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Graph,

        [Parameter(Mandatory = $true)]
        [string]$IssueId
    )

    $successors = @()
    foreach ($nodeId in $Graph.Edges.Keys) {
        if ($IssueId -in $Graph.Edges[$nodeId]) {
            $successors += $nodeId
        }
    }
    return $successors
}

function Test-DependencyCycle {
    <#
    .SYNOPSIS
        Detects cycles in the dependency graph using DFS.
    .PARAMETER Graph
        The dependency graph.
    .OUTPUTS
        Hashtable with HasCycle (bool) and CyclePath (array).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Graph
    )

    $visited = @{}
    $recursionStack = @{}
    $cyclePath = @()

    function Test-CycleDfs {
        param(
            [string]$NodeId,
            [string[]]$Path
        )

        $visited[$NodeId] = $true
        $recursionStack[$NodeId] = $true
        $currentPath = $Path + @($NodeId)

        $predecessors = Get-DependencyPredecessors -Graph $Graph -IssueId $NodeId
        foreach ($pred in $predecessors) {
            if (-not $visited.ContainsKey($pred)) {
                $result = Test-CycleDfs -NodeId $pred -Path $currentPath
                if ($result.HasCycle) {
                    return $result
                }
            } elseif ($recursionStack.ContainsKey($pred) -and $recursionStack[$pred]) {
                $cycleStart = $currentPath.IndexOf($pred)
                if ($cycleStart -ge 0) {
                    $cyclePath = $currentPath[$cycleStart..($currentPath.Count - 1)] + @($pred)
                } else {
                    $cyclePath = $currentPath + @($pred)
                }
                return @{
                    HasCycle = $true
                    CyclePath = $cyclePath
                }
            }
        }

        $recursionStack[$NodeId] = $false
        return @{ HasCycle = $false; CyclePath = @() }
    }

    foreach ($nodeId in $Graph.Nodes.Keys) {
        if (-not $visited.ContainsKey($nodeId)) {
            $result = Test-CycleDfs -NodeId $nodeId -Path @()
            if ($result.HasCycle) {
                return $result
            }
        }
    }

    return @{ HasCycle = $false; CyclePath = @() }
}

function Get-DependencyTopologicalSort {
    <#
    .SYNOPSIS
        Performs topological sort using Kahn's algorithm.
    .PARAMETER Graph
        The dependency graph.
    .OUTPUTS
        Array of issue IDs in execution order.
    .DESCRIPTION
        Issues with no dependencies come first.
        Issues with all dependencies satisfied come next.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Graph
    )

    $cycleCheck = Test-DependencyCycle -Graph $Graph
    if ($cycleCheck.HasCycle) {
        throw "Cannot sort: cycle detected in dependency graph. Cycle: $($cycleCheck.CyclePath -join ' -> ')"
    }

    $inDegree = @{}
    foreach ($nodeId in $Graph.Nodes.Keys) {
        $inDegree[$nodeId] = 0
    }

    foreach ($nodeId in $Graph.Edges.Keys) {
        $inDegree[$nodeId] = $Graph.Edges[$nodeId].Count
    }

    $queue = [System.Collections.Queue]::new()
    foreach ($nodeId in $Graph.Nodes.Keys) {
        if ($inDegree[$nodeId] -eq 0) {
            $queue.Enqueue($nodeId)
        }
    }

    $sorted = @()
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        $sorted += $current

        $successors = Get-DependencySuccessors -Graph $Graph -IssueId $current
        foreach ($succ in $successors) {
            $inDegree[$succ]--
            if ($inDegree[$succ] -eq 0) {
                $queue.Enqueue($succ)
            }
        }
    }

    if ($sorted.Count -ne $Graph.Nodes.Count) {
        throw "Topological sort failed: not all nodes processed (possible cycle)"
    }

    return $sorted
}

function Get-DependencyConcurrencyGroups {
    <#
    .SYNOPSIS
        Groups issues into concurrent execution waves.
    .PARAMETER Graph
        The dependency graph.
    .OUTPUTS
        Array of arrays, each containing issue IDs that can run in parallel.
    .DESCRIPTION
        Wave 1: Issues with no dependencies
        Wave 2: Issues whose dependencies are all in Wave 1
        etc.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Graph
    )

    $cycleCheck = Test-DependencyCycle -Graph $Graph
    if ($cycleCheck.HasCycle) {
        throw "Cannot group: cycle detected in dependency graph"
    }

    $wavesList = [System.Collections.Generic.List[object]]::new()
    $assigned = @{}

    do {
        $currentWave = @()

        foreach ($nodeId in $Graph.Nodes.Keys) {
            if ($assigned.ContainsKey($nodeId)) { continue }

            $predecessors = Get-DependencyPredecessors -Graph $Graph -IssueId $nodeId
            $allDepsAssigned = $true
            foreach ($pred in $predecessors) {
                if (-not $assigned.ContainsKey($pred)) {
                    $allDepsAssigned = $false
                    break
                }
            }

            if ($allDepsAssigned) {
                $currentWave += $nodeId
            }
        }

        if ($currentWave.Count -eq 0 -and $assigned.Count -lt $Graph.Nodes.Count) {
            throw "Deadlock detected: unassigned issues with unsatisfied dependencies"
        }

        $waveIndex = $wavesList.Count
        foreach ($nodeId in $currentWave) {
            $assigned[$nodeId] = $waveIndex
        }

        if ($currentWave.Count -gt 0) {
            $wavesList.Add([object[]]$currentWave)
        }

    } while ($assigned.Count -lt $Graph.Nodes.Count)

    return ,$wavesList.ToArray()
}

function Get-DependencyReadyIssues {
    <#
    .SYNOPSIS
        Gets issues whose dependencies are all completed.
    .PARAMETER Graph
        The dependency graph.
    .PARAMETER CompletedIssues
        Array of completed issue IDs.
    .OUTPUTS
        Array of issue IDs ready to execute.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Graph,

        [Parameter(Mandatory = $false)]
        [string[]]$CompletedIssues = @()
    )

    $ready = @()

    foreach ($nodeId in $Graph.Nodes.Keys) {
        if ($nodeId -in $CompletedIssues) { continue }

        $predecessors = Get-DependencyPredecessors -Graph $Graph -IssueId $nodeId
        $allCompleted = $true
        foreach ($pred in $predecessors) {
            if ($pred -notin $CompletedIssues) {
                $allCompleted = $false
                break
            }
        }

        if ($allCompleted) {
            $ready += $nodeId
        }
    }

    return $ready
}

Export-ModuleMember -Function @(
    'New-DependencyGraph',
    'Add-DependencyNode',
    'Add-DependencyEdge',
    'Get-DependencyPredecessors',
    'Get-DependencySuccessors',
    'Test-DependencyCycle',
    'Get-DependencyTopologicalSort',
    'Get-DependencyConcurrencyGroups',
    'Get-DependencyReadyIssues'
)
