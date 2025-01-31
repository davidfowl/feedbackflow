#!/bin/bash

# The first argument is the issue url
issue=$1
if [ -z "$issue" ]; then
  echo "Usage: $0 <issue_url>"
  exit 1
fi

# Get the Project Item ID from the Content URL

gh api graphql -f query='
query ($url: URI!) {
  resource(url: $url) {
    ... on Issue {
      projectItems(first: 10) {
        nodes {
          id
          project {
            number
          }
        }
      }
    }
  }
}
' -f url="${issue}" |
  jq -r '.data.resource.projectItems.nodes[] | select(.project.number == 475) | .id'
