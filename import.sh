# This script takes a csv file as input.
# The expected format is:
# Title,URL,Comment Count,Unique Commenters,Upvotes,Engagement Score,Years Opened,Frustration Score
# The script will take the first 50 items and add them to the Web Squad .NET 10 Planning project.

# The first argument is the csv file
csv=$1
if [ -z "$csv" ]; then
  echo "Usage: $0 <csv_file>"
  exit 1
fi
projectId=PVT_kwDOAIt-yc4ArJar
fieldId=PVTF_lADOAIt-yc4ArJarzgnNyeQ      # F Score field ID

awk -F "," '{print $2,$8}' $csv | grep -e "^https" | while read url frustration; do
  # Trim the ^M character from the frustration score
  frustration=$(echo $frustration | tr -d '\r')
  # drop the decimal
  if (( ${frustration%.*} >= 10 )); then
    echo "Adding $url to the Web Squad .NET 10 Planning project"
    gh project item-add 475 --owner dotnet --url ${url}
    # Get the Project Item ID from the Content URL
    itemId=$(./get-item-id.sh $url)
    # gh command is broken for float values. https://github.com/cli/cli/issues/10342
    #gh project item-edit --project-id ${projectId} --id ${itemId} --field-id ${fscoreFieldId} --number ${frustration}
    gh api graphql -f query='
      mutation ($itemId: ID!) {
        updateProjectV2ItemFieldValue(input: {projectId: "'$projectId'", itemId: $itemId, fieldId: "'$fieldId'", value: {number: '$frustration'}}) {
          projectV2Item {
            id
          }
        }
      }' -f itemId=${itemId}
  fi
done